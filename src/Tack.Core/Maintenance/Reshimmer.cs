using System.Text.Json;
using Tack.Core.Config;

namespace Tack.Core.Maintenance;

/// <summary>The tack-shim binary to copy per tool, plus any support files that must sit beside each copy
/// (empty for a NativeAOT single-file shim; the .dll/.json set for a framework-dependent build).</summary>
public sealed class ShimPayload
{
    public required string ShimExe { get; init; }
    public IReadOnlyList<string> SupportFiles { get; init; } = Array.Empty<string>();
}

public sealed class ReshimResult
{
    public int ToolsCompiled { get; init; }
    public int ShimsWritten { get; init; }
    /// <summary>Shims already identical to the payload, so left untouched.</summary>
    public int ShimsUnchanged { get; init; }
    public int ShimsPruned { get; init; }
    /// <summary>Files that needed writing or pruning but were locked even after trying to move them aside.
    /// The rest of the reshim still happened; a later reshim picks these up.</summary>
    public IReadOnlyList<string> Locked { get; init; } = Array.Empty<string>();
    /// <summary>True when the shim binary couldn't be found, so resolved.json was written but no shims stamped.</summary>
    public bool ShimPayloadMissing { get; init; }
    public IReadOnlyList<string> ShimNames { get; init; } = Array.Empty<string>();
    /// <summary>Pre-zones binding globs that couldn't be migrated to zones, so resolution ignores them.</summary>
    public IReadOnlyList<string> UnmigratedBindings { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Regenerates the shim state from central config: compiles config.json -> resolved.json, then stamps out
/// one copy of tack-shim.exe per exposed binary name in the shims dir and prunes copies that are no longer
/// exposed. Copy (not symlink) is deliberate - privilege-free on Windows (scope plan section 5.1).
///
/// <para>The shims dir is live: any running shim holds its own exe (and, for a framework-dependent build,
/// tack-shim.dll) open, and AV scanners briefly lock freshly written executables. So a reshim only writes what
/// actually changed, moves a locked file aside rather than failing on it (Windows lets you rename an in-use
/// image, just not overwrite it), and reports anything still stuck instead of throwing.</para>
/// </summary>
public static class Reshimmer
{
    /// <summary>Suffix for in-use files moved aside; deleted by a later reshim once nothing holds them.</summary>
    private const string AsideSuffix = ".tack-old";

    public static ReshimResult Run(CentralConfig config, string shimsDir, string resolvedJsonPath, ShimPayload payload)
    {
        // 1. Compile + write resolved.json (the shim's fast-lookup).
        var resolved = ConfigCompiler.Compile(config);
        string? resolvedDir = Path.GetDirectoryName(resolvedJsonPath);
        if (!string.IsNullOrEmpty(resolvedDir)) Directory.CreateDirectory(resolvedDir);
        WriteAtomically(resolvedJsonPath, JsonSerializer.Serialize(resolved, TackJson.Default.ResolvedConfig));

        // 2. Desired shim names = every exposed binary across every version of every tool.
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in config.Tools.Values)
            foreach (var version in tool.Versions.Values)
                foreach (var exposed in version.Exposes)
                    names.Add(exposed);

        Directory.CreateDirectory(shimsDir);
        DeleteAsideFiles(shimsDir);

        bool payloadMissing = string.IsNullOrEmpty(payload.ShimExe) || !File.Exists(payload.ShimExe);
        int written = 0, unchanged = 0, pruned = 0;
        var locked = new List<string>();

        if (!payloadMissing)
        {
            // Support files (framework-dependent builds) live once in the shims dir; every copy loads them.
            foreach (var f in payload.SupportFiles)
                if (File.Exists(f))
                    Stamp(f, Path.Combine(shimsDir, Path.GetFileName(f)), locked);

            foreach (var name in names)
            {
                switch (Stamp(payload.ShimExe, Path.Combine(shimsDir, name + ".exe"), locked))
                {
                    case StampOutcome.Written: written++; break;
                    case StampOutcome.Unchanged: unchanged++; break;
                }
            }
        }

        // 3. Prune stale shim exes (a name no longer exposed). Support files are .dll/.json, never pruned.
        foreach (var exe in Directory.GetFiles(shimsDir, "*.exe"))
        {
            string baseName = Path.GetFileNameWithoutExtension(exe);
            if (names.Contains(baseName)) continue;
            // A running shim can't be deleted, but it can be renamed away - which stops it resolving just the same.
            if (TryDelete(exe) || MoveAside(exe)) pruned++;
            else locked.Add(exe);
        }

        return new ReshimResult
        {
            ToolsCompiled = config.Tools.Count,
            ShimsWritten = written,
            ShimsUnchanged = unchanged,
            ShimsPruned = pruned,
            Locked = locked,
            ShimPayloadMissing = payloadMissing,
            ShimNames = names.ToList(),
            UnmigratedBindings = ZoneRegistry.Unmigrated(config),
        };
    }

    private enum StampOutcome { Written, Unchanged, Locked }

    // Copy source over dest unless dest is already the same file. File.Copy keeps the source's last-write time,
    // so size + timestamp identifies a copy we made from this exact payload - and a new build (after an update, or
    // a dev rebuild) differs on both.
    private static StampOutcome Stamp(string source, string dest, List<string> locked)
    {
        var src = new FileInfo(source);
        var dst = new FileInfo(dest);
        if (dst.Exists && dst.Length == src.Length && dst.LastWriteTimeUtc == src.LastWriteTimeUtc)
            return StampOutcome.Unchanged;

        try
        {
            File.Copy(source, dest, overwrite: true);
            return StampOutcome.Written;
        }
        catch (IOException)
        {
            // In use (a running shim, or an AV scan). Rename it out of the way and write the new copy in its place.
            if (dst.Exists && MoveAside(dest))
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

    private static bool TryDelete(string path)
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

    // Files moved aside by earlier reshims; each goes as soon as whatever held it has exited.
    private static void DeleteAsideFiles(string shimsDir)
    {
        foreach (var f in Directory.GetFiles(shimsDir, "*" + AsideSuffix))
            TryDelete(f);
    }

    // Write to a temp file and swap it in, so a shim never reads a half-written resolved.json. File.Replace
    // (Win32 ReplaceFile) swaps even while a shim has the old file open - the shim shares delete for exactly this -
    // where a plain rename-over is refused. Retried briefly for an older shim that doesn't share delete yet.
    private static void WriteAtomically(string path, string contents)
    {
        string temp = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temp, contents);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.Replace(temp, path, destinationBackupFileName: null);
                else File.Move(temp, path);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 10)
            {
                Thread.Sleep(50);
            }
            catch
            {
                TryDelete(temp);
                throw;
            }
        }
    }
}
