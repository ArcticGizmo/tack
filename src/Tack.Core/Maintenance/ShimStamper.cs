using System.Security.Cryptography;
using Tack.Core.Config;

namespace Tack.Core.Maintenance;

/// <summary>The tack-shim binary to copy per tool, plus any support files that must sit beside each copy
/// (empty for a NativeAOT single-file shim; the .dll/.json set for a framework-dependent build).</summary>
public sealed class ShimPayload
{
    public required string ShimExe { get; init; }
    public IReadOnlyList<string> SupportFiles { get; init; } = Array.Empty<string>();

    public bool IsMissing => string.IsNullOrEmpty(ShimExe) || !File.Exists(ShimExe);
}

/// <summary>What a shims dir is missing for a set of names: the names to (re)stamp, and whether any support file
/// needs writing.</summary>
public sealed class ShimPlan
{
    public IReadOnlyList<string> Names { get; init; } = Array.Empty<string>();
    public bool SupportFiles { get; init; }
    public bool IsEmpty => Names.Count == 0 && !SupportFiles;
}

public sealed class StampResult
{
    public int Written { get; init; }
    /// <summary>Shims already identical to the payload, so left untouched.</summary>
    public int Unchanged { get; init; }
    /// <summary>Files that needed writing but were locked even after trying to move them aside.</summary>
    public IReadOnlyList<string> Locked { get; init; } = Array.Empty<string>();
    /// <summary>Names refused by <see cref="ShimName"/>; nothing was written for them.</summary>
    public IReadOnlyList<string> Rejected { get; init; } = Array.Empty<string>();
    public bool PayloadMissing { get; init; }
}

public sealed class PruneResult
{
    public IReadOnlyList<string> Pruned { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Locked { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Rejected { get; init; } = Array.Empty<string>();
}

/// <summary>
/// The shims half of a reshim: one copy of tack-shim.exe per command name in this account's shims dir. Copy (not
/// symlink) is deliberate - privilege-free on Windows (scope plan section 5.1). <see cref="Pending"/> and
/// <see cref="Stale"/> are reads; <see cref="Stamp"/> and <see cref="Prune"/> write.
///
/// <para>Every name is checked with <see cref="ShimName"/>, whoever calls, and only <c>&lt;name&gt;.exe</c> for a
/// name it's given is ever written or deleted - never "any exe that isn't on the list", so a stray file in the dir
/// is left alone rather than deleted on a guess.</para>
///
/// <para>The shims dir is live: any running shim holds its own exe (and, for a framework-dependent build,
/// tack-shim.dll) open, and AV scanners briefly lock freshly written executables. So a stamp only writes what
/// actually changed, moves a locked file aside rather than failing on it (Windows lets you rename an in-use
/// image, just not overwrite it), and reports anything still stuck instead of throwing.</para>
/// </summary>
public static class ShimStamper
{
    /// <summary>Suffix for in-use files moved aside; deleted by a later stamp or prune once nothing holds them.</summary>
    private const string AsideSuffix = ".tack-old";

    /// <summary>
    /// The names in <paramref name="names"/> with no shim yet, plus missing support files. With
    /// <paramref name="checkPayload"/>, also those whose content differs from the payload (a new shim build), which
    /// means hashing every copy - so only the explicit repair commands ask for it.
    /// </summary>
    public static ShimPlan Pending(IEnumerable<string> names, string shimsDir, ShimPayload payload, bool checkPayload)
    {
        var hashes = new HashCache();
        bool compare = checkPayload && !payload.IsMissing;
        bool Needs(string source, string dest) =>
            !File.Exists(dest) || (compare && !hashes.Same(source, dest));

        var pending = names.Where(ShimName.IsValid)
            .Where(n => Needs(payload.ShimExe, ShimPath(shimsDir, n)))
            .ToList();
        bool support = payload.SupportFiles.Any(f => File.Exists(f) && Needs(f, Path.Combine(shimsDir, Path.GetFileName(f))));
        return new ShimPlan { Names = pending, SupportFiles = support };
    }

    /// <summary>Shims in the dir for valid names that <paramref name="keep"/> doesn't list: what a
    /// <see cref="Prune"/> after a config change removes.</summary>
    public static List<string> Stale(IEnumerable<string> keep, string shimsDir)
    {
        if (!Directory.Exists(shimsDir)) return new List<string>();
        var wanted = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
        return Directory.GetFiles(shimsDir, "*.exe")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Where(n => ShimName.IsValid(n) && !wanted.Contains(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Write (or refresh) the shims for <paramref name="names"/> and the payload's support files.</summary>
    public static StampResult Stamp(IEnumerable<string> names, string shimsDir, ShimPayload payload)
    {
        var (valid, rejected) = Split(names);
        if (payload.IsMissing)
            return new StampResult { PayloadMissing = true, Rejected = rejected };

        Directory.CreateDirectory(shimsDir);
        DeleteAsideFiles(shimsDir);

        var hashes = new HashCache();
        int written = 0, unchanged = 0;
        var locked = new List<string>();

        // Support files (framework-dependent builds) live once in the shims dir; every copy loads them.
        foreach (var f in payload.SupportFiles)
            if (File.Exists(f))
                StampOne(f, Path.Combine(shimsDir, Path.GetFileName(f)), locked, hashes);

        foreach (var name in valid)
        {
            switch (StampOne(payload.ShimExe, ShimPath(shimsDir, name), locked, hashes))
            {
                case StampOutcome.Written: written++; break;
                case StampOutcome.Unchanged: unchanged++; break;
            }
        }

        return new StampResult { Written = written, Unchanged = unchanged, Locked = locked, Rejected = rejected };
    }

    /// <summary>Remove the shims for exactly <paramref name="names"/>. Support files are never removed: other
    /// shims still load them.</summary>
    public static PruneResult Prune(IEnumerable<string> names, string shimsDir)
    {
        var (valid, rejected) = Split(names);
        if (!Directory.Exists(shimsDir)) return new PruneResult { Rejected = rejected };
        DeleteAsideFiles(shimsDir);

        var pruned = new List<string>();
        var locked = new List<string>();
        foreach (var name in valid)
        {
            string exe = ShimPath(shimsDir, name);
            if (!File.Exists(exe)) continue;
            // A running shim can't be deleted, but it can be renamed away - which stops it resolving just the same.
            if (TryDelete(exe) || MoveAside(exe)) pruned.Add(name);
            else locked.Add(exe);
        }
        return new PruneResult { Pruned = pruned, Locked = locked, Rejected = rejected };
    }

    private static string ShimPath(string shimsDir, string name) => Path.Combine(shimsDir, name + ".exe");

    private static (List<string> Valid, List<string> Rejected) Split(IEnumerable<string> names)
    {
        var valid = new List<string>();
        var rejected = new List<string>();
        foreach (var n in names.Distinct(StringComparer.OrdinalIgnoreCase))
            (ShimName.IsValid(n) ? valid : rejected).Add(n);
        return (valid, rejected);
    }

    private enum StampOutcome { Written, Unchanged, Locked }

    // Copy source over dest unless dest already has the same bytes.
    private static StampOutcome StampOne(string source, string dest, List<string> locked, HashCache hashes)
    {
        if (File.Exists(dest) && hashes.Same(source, dest))
            return StampOutcome.Unchanged;

        try
        {
            File.Copy(source, dest, overwrite: true);
            return StampOutcome.Written;
        }
        catch (IOException)
        {
            // In use (a running shim, or an AV scan). Rename it out of the way and write the new copy in its place.
            if (File.Exists(dest) && MoveAside(dest))
            {
                try
                {
                    File.Copy(source, dest, overwrite: false);
                    return StampOutcome.Written;
                }
                catch (IOException) { /* fall through to locked */ }
            }
            locked.Add(dest);
            return StampOutcome.Locked;
        }
        catch (UnauthorizedAccessException)
        {
            locked.Add(dest);
            return StampOutcome.Locked;
        }
    }

    // Hashes each source once per call; a stamp compares one payload against every copy of it.
    private sealed class HashCache
    {
        private readonly Dictionary<string, byte[]> _sources = new(StringComparer.OrdinalIgnoreCase);

        public bool Same(string source, string dest)
        {
            if (!_sources.TryGetValue(source, out var expected))
                _sources[source] = expected = Hash(source);
            try { return new FileInfo(dest).Length == new FileInfo(source).Length && Hash(dest).AsSpan().SequenceEqual(expected); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }

        private static byte[] Hash(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return SHA256.HashData(fs);
        }
    }

    private static bool MoveAside(string path)
    {
        try
        {
            File.Move(path, $"{path}.{Guid.NewGuid():N}{AsideSuffix}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Files moved aside earlier; each goes as soon as whatever held it has exited.
    private static void DeleteAsideFiles(string shimsDir)
    {
        foreach (var f in Directory.GetFiles(shimsDir, "*" + AsideSuffix))
            TryDelete(f);
    }
}
