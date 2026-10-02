using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Tack.Core.Config;
using Tack.Core.Resolution;

namespace Tack.Core.Installs;

public enum InstallStage { Downloading, Verifying, Unpacking, Placing, PostInstall }

/// <summary>Where an install has got to. <see cref="Download"/> is set while downloading; <see cref="Detail"/> names
/// the post-install step.</summary>
public readonly record struct InstallProgress(InstallStage Stage, DownloadProgress? Download = null, string? Detail = null);

/// <summary>The marker a managed version's folder carries (<see cref="Installer.MarkerFile"/>). Its presence, and its
/// tool and version matching the folder's names, is what lets tack delete the folder (I7).</summary>
public sealed class InstallMarker
{
    public string Tool { get; set; } = "";
    public string Version { get; set; } = "";
    public string Source { get; set; } = "";
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public DateTimeOffset InstalledAt { get; set; }
}

/// <summary>What a removal left behind: null when the folder is gone, otherwise the trash folder that's still there
/// (retried on the next install or <c>doctor --fix</c>).</summary>
public sealed record RemoveResult(string? Leftover);

/// <summary>
/// Puts managed versions on disk and takes them off again (see docs/m10-managed-installs-plan.md, I3 and I7 to I11).
/// Registering them is the caller's job, through <see cref="ToolRegistry.Register"/>. Everything happens under one
/// lock file per installs folder, so installs and removals never overlap, even across processes.
///
/// <para>An install: sweep leftovers from killed runs, download into <c>.staging</c>, check the SHA-256, unpack the
/// whole archive (the framework refuses entries that escape), write the marker, rename the archive's top folder to
/// <c>&lt;tool&gt;\&lt;version&gt;</c>, run the post-install steps there (pip's launchers embed their own path, so they
/// can't be made in staging), then keep the exposed names that exist. A failure before the rename deletes the staging
/// folder; one after it sends the placed folder to <c>.trash</c>. Nothing is ever left half-installed in place.</para>
/// </summary>
public sealed class Installer
{
    public const string MarkerFile = ".tack-install.json";

    private readonly string _root;
    private readonly IDownloader _downloader;
    private readonly IProcessRunner _runner;
    private readonly Func<DateTimeOffset> _now;

    public Installer(string installsDir, IDownloader downloader, IProcessRunner runner, Func<DateTimeOffset>? now = null)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installsDir));
        _downloader = downloader;
        _runner = runner;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    /// <summary>Unpacked size above which an archive is refused: far beyond any real Node or Python, short of filling a disk.</summary>
    public long MaxUnpackedBytes { get; init; } = 4L * 1024 * 1024 * 1024;

    /// <summary>Entry count above which an archive is refused (Node has about 2,500, Python about 4,000).</summary>
    public int MaxEntries { get; init; } = 200_000;

    public string Root => _root;
    public string StagingDir => Path.Combine(_root, ".staging");
    public string TrashDir => Path.Combine(_root, ".trash");

    /// <summary>Where <paramref name="tool"/>@<paramref name="version"/> is installed.</summary>
    public string FolderFor(string tool, string version) => Path.Combine(_root, tool, version);

    public async Task<InstalledVersion> InstallAsync(InstallPlan plan, IProgress<InstallProgress>? progress = null,
        CancellationToken cancel = default)
    {
        if (ShimName.Problem(plan.Tool) is { } badTool) throw new InstallException($"'{plan.Tool}' can't be a tool name: {badTool}.");
        if (!RemoteVersion.IsPlain(plan.Version)) throw new InstallException($"'{plan.Version}' isn't a version tack can install.");
        string sha = Checksums.Sha256(plan.Sha256) ?? throw new InstallException($"'{plan.Sha256}' isn't a SHA-256.");
        if (plan.Url.Scheme != Uri.UriSchemeHttps) throw new InstallException($"tack only downloads over HTTPS, and {plan.Url} isn't.");

        string target = FolderFor(plan.Tool, plan.Version);
        using (TakeLock())
        {
            SweepStaging();
            SweepTrash();
            if (Directory.Exists(target) || File.Exists(target))
                throw new InstallException($"{target} already exists. If nothing is registered for it, tack doctor --fix removes it.");

            string work = Path.Combine(StagingDir, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                string archive = Path.Combine(work, "archive.zip");
                progress?.Report(new InstallProgress(InstallStage.Downloading, new DownloadProgress(0, null)));
                var download = progress is null ? null : new Progress<DownloadProgress>(p => progress.Report(new InstallProgress(InstallStage.Downloading, p)));
                await _downloader.DownloadFileAsync(plan.Url, archive, download, cancel).ConfigureAwait(false);

                progress?.Report(new InstallProgress(InstallStage.Verifying));
                string actual = await Sha256Of(archive, cancel).ConfigureAwait(false);
                if (actual != sha)
                    throw new InstallException($"the download from {plan.Url} doesn't match its published SHA-256 " +
                        $"(expected {sha}, got {actual}), so tack deleted it. Try again; if it keeps happening, something " +
                        $"between you and {plan.Url.Host} is changing it.");

                progress?.Report(new InstallProgress(InstallStage.Unpacking));
                string unpacked = Path.Combine(work, "unpacked");
                Unpack(archive, unpacked);
                string home = plan.TopFolder is null ? unpacked : Path.Combine(unpacked, plan.TopFolder);
                if (!Directory.Exists(home))
                    throw new InstallException($"{plan.Url} doesn't contain the folder tack expects ({plan.TopFolder}), so it wasn't installed.");

                progress?.Report(new InstallProgress(InstallStage.Placing));
                WriteMarker(home, plan, sha);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                Directory.Move(home, target);
            }
            finally
            {
                TryDelete(work);
            }

            try
            {
                return await Finish(plan, target, sha, progress, cancel).ConfigureAwait(false);
            }
            catch
            {
                Discard(target);
                throw;
            }
        }
    }

    // Post-install in the final folder, then what it exposes.
    private async Task<InstalledVersion> Finish(InstallPlan plan, string target, string sha, IProgress<InstallProgress>? progress,
        CancellationToken cancel)
    {
        foreach (var step in plan.PostInstall)
        {
            progress?.Report(new InstallProgress(InstallStage.PostInstall, Detail: step.Description));
            var result = await _runner.RunAsync(Path.Combine(target, step.Exe), step.Args, target, cancel).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new InstallException($"{plan.Tool} {plan.Version}: {step.Description} failed (exit code {result.ExitCode}), " +
                    $"so it wasn't installed.{Tail(result.Output)}");
        }

        var extra = plan.ExtraBinDirs.Select(d => Path.Combine(target, d)).ToList();
        var binDirs = extra.Prepend(target).ToList();
        var exposes = plan.Exposes.Where(name => BinaryLocator.Locate(binDirs, name, File.Exists) is not null).ToList();
        if (!exposes.Contains(plan.Tool, StringComparer.OrdinalIgnoreCase))
            throw new InstallException($"{plan.Url} has no {plan.Tool} executable where tack expects it, so it wasn't installed.");

        return new InstalledVersion
        {
            BinDir = target,
            ExtraBinDirs = extra.Count > 0 ? extra : null,
            Exposes = exposes,
            Install = new InstallReceipt { Source = plan.Source, Url = plan.Url.ToString(), Sha256 = sha, InstalledAt = _now() },
        };
    }

    /// <summary>
    /// Delete a managed version's folder, if tack installed it (<see cref="OwnershipProblem"/>). It's renamed into
    /// <c>.trash</c> first: Windows refuses that while anything is running from the folder or has it as its current
    /// directory, so an in-use version fails here, untouched, before the caller changes any config.
    /// </summary>
    public RemoveResult Remove(string folder)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        using (TakeLock())
        {
            if (OwnershipProblem(full) is { } why)
                throw new InstallException($"tack won't delete {full}: {why}.");

            Directory.CreateDirectory(TrashDir);
            string trash = Path.Combine(TrashDir, Guid.NewGuid().ToString("N"));
            try { Directory.Move(full, trash); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new InstallException($"{full} is in use, so nothing was removed. Close anything running from it, " +
                    "including a terminal whose current directory is inside it, then try again.", e);
            }
            return new RemoveResult(TryDelete(trash) ? null : trash);
        }
    }

    /// <summary>Why tack mustn't delete <paramref name="folder"/>, or null if it may (I7): it has to be a
    /// <c>&lt;tool&gt;\&lt;version&gt;</c> folder directly inside the installs folder, a real folder rather than a link,
    /// with a marker naming that same tool and version.</summary>
    public string? OwnershipProblem(string folder)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        string relative = Path.GetRelativePath(_root, full);
        if (Path.IsPathRooted(relative) || relative == "." || relative.StartsWith("..", StringComparison.Ordinal))
            return $"it isn't inside tack's installs folder ({_root})";
        string[] parts = relative.Split(Path.DirectorySeparatorChar);
        if (parts.Length != 2 || parts[0].StartsWith('.'))
            return $"it isn't a <tool>\\<version> folder in {_root}";
        if (!Directory.Exists(full))
            return "it doesn't exist";
        if (IsLink(full) || IsLink(Path.Combine(_root, parts[0])))
            return "it's a link, not a folder tack made";

        string markerPath = Path.Combine(full, MarkerFile);
        if (!File.Exists(markerPath))
            return $"it has no {MarkerFile}, so tack didn't install it";
        InstallMarker? marker;
        try { marker = JsonSerializer.Deserialize(File.ReadAllText(markerPath), TackJson.Default.InstallMarker); }
        catch (Exception e) when (e is JsonException or IOException) { return $"its {MarkerFile} can't be read ({e.Message})"; }
        if (marker is null || !string.Equals(marker.Tool, parts[0], StringComparison.OrdinalIgnoreCase)
                           || !string.Equals(marker.Version, parts[1], StringComparison.OrdinalIgnoreCase))
            return $"its {MarkerFile} is for {marker?.Tool}@{marker?.Version}, not {parts[0]}@{parts[1]}";
        return null;
    }

    /// <summary>Delete whatever is in <c>.trash</c> (what a removal couldn't finish). Returns what's still there.</summary>
    public IReadOnlyList<string> SweepTrash() => Sweep(TrashDir);

    private IReadOnlyList<string> SweepStaging() => Sweep(StagingDir);

    private static IReadOnlyList<string> Sweep(string dir)
    {
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        var left = new List<string>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            if (!TryDelete(entry)) left.Add(entry);
        return left;
    }

    // A placed version that failed after the rename: out of the way first (so a retry isn't blocked), then gone.
    private void Discard(string target)
    {
        try
        {
            Directory.CreateDirectory(TrashDir);
            string trash = Path.Combine(TrashDir, Guid.NewGuid().ToString("N"));
            Directory.Move(target, trash);
            TryDelete(trash);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDelete(target);
        }
    }

    private void Unpack(string archive, string destination)
    {
        try
        {
            using (var zip = ZipFile.OpenRead(archive))
            {
                if (zip.Entries.Count > MaxEntries)
                    throw new InstallException($"the archive has {zip.Entries.Count} entries, more than tack accepts ({MaxEntries}).");
                long total = 0;
                foreach (var entry in zip.Entries) total += entry.Length;
                if (total > MaxUnpackedBytes)
                    throw new InstallException($"the archive unpacks to {total / (1024 * 1024)} MB, more than tack accepts ({MaxUnpackedBytes / (1024 * 1024)} MB).");
            }
            // Refuses any entry that would land outside the destination (no zip slip).
            ZipFile.ExtractToDirectory(archive, destination);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new InstallException($"the archive couldn't be unpacked: {e.Message}", e);
        }
    }

    private void WriteMarker(string home, InstallPlan plan, string sha)
    {
        var marker = new InstallMarker
        {
            Tool = plan.Tool, Version = plan.Version, Source = plan.Source, Url = plan.Url.ToString(), Sha256 = sha, InstalledAt = _now(),
        };
        File.WriteAllText(Path.Combine(home, MarkerFile), JsonSerializer.Serialize(marker, TackJson.Default.InstallMarker));
    }

    private static async Task<string> Sha256Of(string path, CancellationToken cancel)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancel).ConfigureAwait(false));
    }

    // One install or removal at a time per installs folder, across processes. A lock file rather than a mutex,
    // because a mutex belongs to a thread and an install awaits across several.
    private FileStream TakeLock()
    {
        Directory.CreateDirectory(_root);
        try
        {
            return new FileStream(Path.Combine(_root, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                1, FileOptions.DeleteOnClose);
        }
        // A sharing violation while it's held; access denied in the moment it's being deleted on close.
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new InstallException("another tack install or removal is running. Wait for it to finish, then try again.", e);
        }
    }

    private static bool IsLink(string path) =>
        new DirectoryInfo(path) is { Exists: true } d && d.Attributes.HasFlag(FileAttributes.ReparsePoint);

    private static bool TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // The end of a failed step's output, which is where the reason usually is.
    private static string Tail(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "" : "\n" + string.Join('\n', lines.TakeLast(10));
    }
}
