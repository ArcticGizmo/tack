using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;
using Velopack;
using Velopack.Sources;

// tack-msi-spike
//
// Answers the open questions for a per-machine (Program Files) Velopack MSI:
//   1. Do the install / update / uninstall hooks run elevated, and as whom?
//   2. Can those hooks write the install dir (where admin-owned shims would live) and the system PATH key?
//   3. Does `update` against a newer package in Program Files prompt for UAC, and does the post-update hook
//      then run elevated?
// Every hook appends one report to its own file under %ProgramData%\TackMsiSpike (one file per event + pid,
// because a file created by SYSTEM can't be appended to by a standard user there). Nothing is modified:
// the PATH key is only opened for write, never written.

VelopackApp.Build()
    .OnAfterInstallFastCallback(v => Report("after-install", v.ToString(), writeMarkers: true))
    .OnBeforeUpdateFastCallback(v => Report("before-update", v.ToString()))
    .OnAfterUpdateFastCallback(v => Report("after-update", v.ToString()))
    .OnBeforeUninstallFastCallback(v => Report("before-uninstall", v.ToString()))
    .OnFirstRun(v => Report("first-run", v.ToString()))
    .Run();

switch (args.FirstOrDefault())
{
    case "whoami":
        Console.Write(Describe("whoami", Version()));
        return 0;

    // Which install-time markers are still here: run after an update to see what Velopack's swap kept.
    case "check":
        foreach (var (where, path) in MarkerPaths())
            Console.WriteLine($"marker in {where,-7} {(File.Exists(path) ? "present" : "MISSING")}  {path}");
        return 0;

    // Blocking rather than `await`: an awaited top-level statement moves VelopackApp.Run() into an async state
    // machine, which vpk warns is not the real entry point.
    case "update" when args.Length >= 2:
        return Update(args[1]).GetAwaiter().GetResult();

    default:
        Console.WriteLine($"tack-msi-spike {Version()}");
        Console.WriteLine("usage: tack-msi-spike whoami | update <feedDir>");
        Console.WriteLine($"hook reports: {LogDir()}");
        return 0;
}

static async Task<int> Update(string feedDir)
{
    var mgr = new UpdateManager(new SimpleFileSource(new DirectoryInfo(feedDir)));
    Console.WriteLine($"installed: {mgr.IsInstalled}  current: {mgr.CurrentVersion}  appDir: {AppContext.BaseDirectory}");
    if (!mgr.IsInstalled) return 1;

    var info = await mgr.CheckForUpdatesAsync();
    if (info is null)
    {
        Console.WriteLine("up to date");
        return 0;
    }

    Console.WriteLine($"downloading {info.TargetFullRelease.Version}...");
    await mgr.DownloadUpdatesAsync(info);

    // The same call tack's `update` makes: the updater waits for this process to exit, then applies.
    mgr.WaitExitThenApplyUpdates(info.TargetFullRelease, silent: true, restart: false);
    Console.WriteLine("applying as this process exits - watch for a UAC prompt");
    return 0;
}

static string Version() =>
    typeof(Program).Assembly.GetName().Version?.ToString() ?? "?";

static string LogDir() => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TackMsiSpike");

static void Report(string hook, string version, bool writeMarkers = false)
{
    try
    {
        string report = Describe(hook, version);
        if (writeMarkers) report += WriteMarkers();
        Directory.CreateDirectory(LogDir());
        File.WriteAllText(Path.Combine(LogDir(), $"{DateTime.Now:yyyyMMdd-HHmmss}-{hook}-{Environment.ProcessId}.log"),
            report);
    }
    catch
    {
        // A hook must never fail the install; a missing report is itself an answer.
    }
}

static string Describe(string hook, string version)
{
    using var id = WindowsIdentity.GetCurrent();
    bool elevated = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    string appDir = AppContext.BaseDirectory;

    return $"""
        hook:              {hook}
        version:           {version}
        time:              {DateTime.Now:o}
        user:              {id.Name}
        is SYSTEM:         {id.IsSystem}
        elevated:          {elevated}
        process:           {Environment.ProcessPath}
        app dir:           {appDir}
        LocalAppData:      {Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}
        can write app dir: {CanWriteDir(appDir)}
        can open HKLM env for write: {CanOpenMachineEnvForWrite()}

        """;
}

// Where tack could keep files it adds after install (shims): the install root beside Update.exe, or current\,
// which Velopack swaps out on every update.
static (string Where, string Path)[] MarkerPaths()
{
    string current = AppContext.BaseDirectory.TrimEnd('\\', '/');
    string root = Path.GetDirectoryName(current) ?? current;
    return new[]
    {
        ("root", Path.Combine(root, "tack-spike-marker.txt")),
        ("current", Path.Combine(current, "tack-spike-marker.txt")),
    };
}

static string WriteMarkers()
{
    var lines = new System.Text.StringBuilder();
    foreach (var (where, path) in MarkerPaths())
    {
        try
        {
            File.WriteAllText(path, $"written by after-install at {DateTime.Now:o}");
            lines.AppendLine($"marker in {where}: written  {path}");
        }
        catch (Exception ex)
        {
            lines.AppendLine($"marker in {where}: FAILED ({ex.GetType().Name})  {path}");
        }
    }
    return lines.ToString();
}

static string CanWriteDir(string dir)
{
    string probe = Path.Combine(dir, $".write-probe-{Guid.NewGuid():N}");
    try
    {
        File.WriteAllText(probe, "");
        File.Delete(probe);
        return "yes";
    }
    catch (Exception ex)
    {
        return $"no ({ex.GetType().Name})";
    }
}

[SupportedOSPlatform("windows")]
static string CanOpenMachineEnvForWrite()
{
    try
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", writable: true);
        return key is null ? "no (missing)" : "yes";
    }
    catch (Exception ex)
    {
        return $"no ({ex.GetType().Name})";
    }
}
