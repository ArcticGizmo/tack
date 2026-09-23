using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

// tack-shim
//
// A single tiny NativeAOT proxy exe, copied once per exposed tool binary (node.exe, npm.exe, python.exe...).
// It works out which tool it stands in for from its OWN filename, resolves the correct real binary for the
// current directory from a pre-compiled resolved.json, then spawns that binary and proxies it faithfully:
// forwards args + std streams, ignores Ctrl-C itself (lets the child own the interrupt), waits, and exits
// with the child's exit code.
//
// Resolution here is deliberately minimal (M1: one hard-registered tool + a directory map). Full precedence,
// tack.yml walk-up and central bindings are M2. resolved.json schema:
//   { "tools": { "node": { "default": "C:\\...\\node.cmd",
//                          "dirs": { "C:\\work\\projB": "C:\\...\\other\\node.cmd" } } } }
// The shim picks the longest "dirs" key that is an ancestor of (or equal to) the cwd; else "default".

try
{
    // 1. Who am I proxying? basename of my own exe, minus .exe.
    string self = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
    string tool = Path.GetFileNameWithoutExtension(self);
    Debug($"tool='{tool}' self='{self}'");

    // 2. Load the pre-compiled resolved.json (source-gen, AOT-safe).
    string? resolvedPath = FindResolved();
    if (resolvedPath is null)
        return Fail("no resolved.json found (set TACK_RESOLVED, or place it beside the shim / in %LOCALAPPDATA%\\tack)");

    ResolvedConfig? config;
    try
    {
        using var fs = File.OpenRead(resolvedPath);
        config = JsonSerializer.Deserialize(fs, ShimJson.Default.ResolvedConfig);
    }
    catch (Exception ex)
    {
        return Fail($"could not read {resolvedPath}: {ex.Message}");
    }

    if (config?.Tools is null || !config.Tools.TryGetValue(tool, out var entry) || entry is null)
        return Fail($"tool '{tool}' is not registered in {resolvedPath}");

    // 3. Resolve the target for THIS directory.
    string cwd = Environment.CurrentDirectory;
    string? target = ResolveTarget(entry, cwd);
    if (string.IsNullOrEmpty(target))
        return Fail($"tool '{tool}' has no target for '{cwd}' and no default");

    if (!File.Exists(target))
        return Fail($"resolved target does not exist on disk: {target}");

    Debug($"cwd='{cwd}' -> target='{target}' args=[{string.Join(", ", args)}]");

    // 4. Ignore Ctrl-C in the shim so the CHILD owns the interrupt, then exec + proxy.
    Native.IgnoreConsoleInterrupts();
    return Exec(target, args);
}
catch (Exception ex)
{
    return Fail($"unexpected: {ex.Message}");
}

// ---- exec proxy ----------------------------------------------------------------------------------

// Spawn the target as a child, inheriting our console + std handles, and return ITS exit code.
//
// Windows sharp edges this proxy handles:
//  * There is no execve: we can't become the target, so we spawn a child and must proxy it faithfully.
//  * A .cmd/.bat is NOT a PE image, so CreateProcess (UseShellExecute=false) rejects it outright
//    ("not a valid application"). Such targets must go through cmd.exe /c. (A full path to a .cmd only
//    "just resolves via the associated interpreter" under UseShellExecute=true, which spawns a new window
//    and breaks handle inheritance - so we route .cmd/.bat via cmd.exe here.)
//  * No redirection + UseShellExecute=false => the child inherits our stdin/stdout/stderr and console. No
//    new window.
static int Exec(string target, string[] forwarded)
{
    string ext = Path.GetExtension(target);
    bool viaCmd = ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase);

    var psi = new ProcessStartInfo
    {
        FileName = viaCmd ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe" : target,
        UseShellExecute = false,   // inherit handles, no shell, no new window
    };
    if (viaCmd)
    {
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(target);
    }
    foreach (var a in forwarded) psi.ArgumentList.Add(a);

    using var child = Process.Start(psi);
    if (child is null) return Fail("failed to start target process");
    child.WaitForExit();
    return child.ExitCode;
}

// ---- resolution ----------------------------------------------------------------------------------

// Longest matching "dirs" key that is the cwd or an ancestor of it (case-insensitive, Windows). Else default.
static string? ResolveTarget(ToolEntry entry, string cwd)
{
    string best = "";
    string? bestTarget = null;
    if (entry.Dirs is not null)
    {
        string cwdFull = NormalizeDir(cwd);
        foreach (var (dir, tgt) in entry.Dirs)
        {
            string d = NormalizeDir(dir);
            bool isMatch = cwdFull.Equals(d, StringComparison.OrdinalIgnoreCase)
                        || cwdFull.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (isMatch && d.Length > best.Length)
            {
                best = d;
                bestTarget = tgt;
            }
        }
    }
    return bestTarget ?? entry.Default;
}

static string NormalizeDir(string p)
{
    try { p = Path.GetFullPath(p); } catch { /* leave as-is */ }
    return p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}

static string? FindResolved()
{
    if (Environment.GetEnvironmentVariable("TACK_RESOLVED") is { Length: > 0 } env && File.Exists(env))
        return env;

    string beside = Path.Combine(AppContext.BaseDirectory, "resolved.json");
    if (File.Exists(beside)) return beside;

    string local = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "tack", "resolved.json");
    return File.Exists(local) ? local : null;
}

// ---- misc ---------------------------------------------------------------------------------------

static void Debug(string msg)
{
    if (Environment.GetEnvironmentVariable("TACK_SHIM_DEBUG") is { Length: > 0 })
        Console.Error.WriteLine($"[tack-shim] {msg}");
}

static int Fail(string msg)
{
    Console.Error.WriteLine($"tack-shim: {msg}");
    return 127; // command-not-found-ish. (v1 will passthrough to next-on-PATH here -- that's M2.)
}

// ---- interop -------------------------------------------------------------------------------------

internal static partial class Native
{
    // Install a console control handler that returns TRUE (handled) for Ctrl-C / Ctrl-Break, so the
    // interrupt is NOT delivered to the shim. The child shares our console and receives the signal too;
    // letting the child handle it (and only then observing its exit code) is what keeps `node`'s own SIGINT
    // semantics intact and stops the wrapper from dying before the child cleans up. Getting this wrong is
    // the classic way toy shims leave orphans or swallow exit codes.
    public static unsafe void IgnoreConsoleInterrupts()
    {
        try { SetConsoleCtrlHandler(&CtrlHandler, add: true); }
        catch { /* not attached to a console; fine */ }
    }

    [UnmanagedCallersOnly]
    private static int CtrlHandler(uint ctrlType) => 1; // TRUE: we "handled" it (ignore); child still gets it.

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetConsoleCtrlHandler(delegate* unmanaged<uint, int> handler,
        [MarshalAs(UnmanagedType.Bool)] bool add);
}

// resolved.json is tool-managed JSON, read via source generator so AOT/trimming can't break it.
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ResolvedConfig))]
internal partial class ShimJson : JsonSerializerContext { }

internal sealed class ResolvedConfig
{
    public Dictionary<string, ToolEntry>? Tools { get; set; }
}

internal sealed class ToolEntry
{
    public string? Default { get; set; }
    public Dictionary<string, string>? Dirs { get; set; }
}
