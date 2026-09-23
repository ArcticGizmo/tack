using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Tack.Core;
using Tack.Core.Config;
using Tack.Core.Resolution;

// tack-shim
//
// A single tiny NativeAOT proxy exe, copied once per exposed tool binary (node.exe, npm.exe, python.exe...).
// It reads its own filename to learn which binary it stands in for, asks Tack.Core's resolver which version
// that binary should be for the current directory, locates the real executable, and proxies it faithfully:
// forwards args + std streams, ignores Ctrl-C itself (lets the child own the interrupt), waits, and exits
// with the child's exit code.
//
// All resolution logic lives in Tack.Core (the resolver, glob, version match, mini tack.yml parser). The
// shim is a thin front-end: filename -> Resolver -> locate -> exec. It reads only the compiled resolved.json.

try
{
    // 1. Who am I proxying? basename of my own exe, minus .exe.
    string self = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
    string exposed = Path.GetFileNameWithoutExtension(self);
    Debug($"exposed='{exposed}' self='{self}'");

    // 2. Load the pre-compiled resolved.json (source-gen, AOT-safe).
    string? resolvedPath = FindResolved();
    if (resolvedPath is null)
        return Fail("no resolved.json found (set TACK_RESOLVED, or place it beside the shim / in %LOCALAPPDATA%\\tack)");

    ResolvedConfig? config;
    try
    {
        using var fs = File.OpenRead(resolvedPath);
        config = JsonSerializer.Deserialize(fs, TackJson.Default.ResolvedConfig);
    }
    catch (Exception ex)
    {
        return Fail($"could not read {resolvedPath}: {ex.Message}");
    }
    if (config is null) return Fail($"empty or invalid config: {resolvedPath}");

    // 3. Resolve which version this binary is for THIS directory (Core owns the precedence).
    var resolver = new Resolver(config);
    var ctx = new ResolverContext
    {
        GetEnv = Environment.GetEnvironmentVariable,
        ReadFileOrNull = p => { try { return File.Exists(p) ? File.ReadAllText(p) : null; } catch { return null; } },
    };
    var res = resolver.Resolve(exposed, Environment.CurrentDirectory, ctx);
    Debug($"source={res.Source} tool={res.Tool} version={res.Version} binDir={res.BinDir} :: {res.Detail}");

    // Install the Ctrl-C handler once, before any child is spawned.
    Native.IgnoreConsoleInterrupts();

    switch (res.Source)
    {
        // A rule named a version that isn't installed -> a real misconfiguration; fail loudly, never guess.
        case ResolutionSource.VersionNotInstalled:
            return Fail(res.Detail ?? "resolved version is not installed");

        // Nothing resolved, or a stale shim for an unregistered name. Default behaviour is to stay invisible:
        // pass through to the next matching binary on PATH. `error` mode fails instead.
        case ResolutionSource.Passthrough:
        case ResolutionSource.Unregistered:
            if (string.Equals(config.Settings.NoResolution, "error", StringComparison.OrdinalIgnoreCase))
                return Fail(res.Detail ?? $"'{exposed}' did not resolve for this directory");
            return Passthrough(exposed, args);

        // Resolved to a concrete installed version: locate the real binary in its binDir and exec it.
        default:
            string? target = BinaryLocator.Locate(res.BinDir!, exposed, File.Exists);
            if (target is null)
                return Fail($"{exposed} {res.Version}: no '{exposed}' executable found in {res.BinDir}");
            return Exec(target, args);
    }
}
catch (Exception ex)
{
    return Fail($"unexpected: {ex.Message}");
}

// ---- passthrough -------------------------------------------------------------------------------

// tack stays invisible where it isn't configured: exec the next matching binary on PATH, skipping our own
// shims dir so we don't recurse into ourselves.
static int Passthrough(string exposed, string[] forwarded)
{
    string shimsDir = Norm(AppContext.BaseDirectory);
    string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";

    foreach (var entry in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (Norm(entry) == shimsDir) continue; // skip ourselves
        string? found = BinaryLocator.Locate(entry, exposed, File.Exists);
        if (found is not null)
        {
            Debug($"passthrough -> {found}");
            return Exec(found, forwarded);
        }
    }
    return Fail($"'{exposed}' did not resolve for this directory and no fallback was found on PATH");
}

static string Norm(string p)
{
    try { return Path.GetFullPath(p).TrimEnd('\\', '/').ToLowerInvariant(); }
    catch { return p.TrimEnd('\\', '/').ToLowerInvariant(); }
}

// ---- exec proxy ---------------------------------------------------------------------------------

// Spawn the target as a child, inheriting our console + std handles, and return ITS exit code. A .cmd/.bat
// is not a PE image, so CreateProcess (UseShellExecute=false) rejects it; those go through cmd.exe /c.
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

// ---- config discovery ---------------------------------------------------------------------------

static string? FindResolved()
{
    if (Environment.GetEnvironmentVariable("TACK_RESOLVED") is { Length: > 0 } env && File.Exists(env))
        return env;

    string beside = Path.Combine(AppContext.BaseDirectory, "resolved.json");
    if (File.Exists(beside)) return beside;

    return File.Exists(TackPaths.ResolvedJson) ? TackPaths.ResolvedJson : null;
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
    return 127;
}

// ---- interop ------------------------------------------------------------------------------------

internal static partial class Native
{
    // Ignore Ctrl-C / Ctrl-Break in the shim so the interrupt is delivered to the child (which shares our
    // console), not to the wrapper. Getting this wrong is how toy shims leave orphans or swallow exit codes.
    public static unsafe void IgnoreConsoleInterrupts()
    {
        try { SetConsoleCtrlHandler(&CtrlHandler, add: true); }
        catch { /* not attached to a console; fine */ }
    }

    [UnmanagedCallersOnly]
    private static int CtrlHandler(uint ctrlType) => 1; // TRUE: "handled" (ignored); child still gets it.

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetConsoleCtrlHandler(delegate* unmanaged<uint, int> handler,
        [MarshalAs(UnmanagedType.Bool)] bool add);
}
