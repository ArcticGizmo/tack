using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Tack.Core;
using Tack.Core.Config;
using Tack.Core.Diagnostics;
using Tack.Core.Resolution;

// tack-shim
//
// A single tiny NativeAOT proxy exe, copied once per exposed tool binary (node.exe, npm.exe, python.exe...).
// It reads its own filename to learn which binary it stands in for, asks Tack.Core's resolver which version
// that binary should be for the current directory, locates the real executable, and proxies it faithfully:
// forwards args + std streams, ignores Ctrl-C itself (lets the child own the interrupt), waits, and exits
// with the child's exit code. With `tack log on` it first appends the call - and the chain of processes that
// made it - to the invocation log.
//
// All resolution logic lives in Tack.Core (the resolver, zone paths, version match, mini tack.yml parser). The
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
        // Share write + delete so a reshim swapping in a new resolved.json is never blocked by a shim mid-read.
        using var fs = new FileStream(resolvedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
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

    // 4. Decide what to run (or why not), record it if `tack log on`, then act.
    var (target, error) = Decide(exposed, res, config);
    if (config.Settings.Log) LogInvocation(ShimLog.PathFor(resolvedPath), exposed, args, res, target, error);

    // Install the Ctrl-C handler once, before any child is spawned.
    Native.IgnoreConsoleInterrupts();

    return target is null ? Fail(error!) : Exec(target, args);
}
catch (Exception ex)
{
    return Fail($"unexpected: {ex.Message}");
}

// ---- dispatch decision --------------------------------------------------------------------------

// Exactly one of Target (the executable to run) and Error (why we won't) is set.
static (string? Target, string? Error) Decide(string exposed, Resolution res, ResolvedConfig config)
{
    switch (res.Source)
    {
        // A rule named a version that isn't installed -> a real misconfiguration; fail loudly, never guess.
        case ResolutionSource.VersionNotInstalled:
            return (null, res.Detail ?? "resolved version is not installed");

        // A none zone: tack is switched off for this tool here, on purpose - so always pass through, whatever
        // noResolution says.
        case ResolutionSource.ZoneNone:
            return Passthrough(exposed);

        // Nothing resolved, or a stale shim for an unregistered name. Default behaviour is to stay invisible:
        // pass through to the next matching binary on PATH. `error` mode fails instead.
        case ResolutionSource.Passthrough:
        case ResolutionSource.Unregistered:
            if (string.Equals(config.Settings.NoResolution, "error", StringComparison.OrdinalIgnoreCase))
                return (null, res.Detail ?? $"'{exposed}' did not resolve for this directory");
            return Passthrough(exposed);

        // Resolved to a concrete installed version: locate the real binary in its binDir.
        default:
            string? target = BinaryLocator.Locate(res.BinDir!, exposed, File.Exists);
            return target is null
                ? (null, $"{exposed} {res.Version}: no '{exposed}' executable found in {res.BinDir}")
                : (target, null);
    }
}

// ---- passthrough -------------------------------------------------------------------------------

// tack stays invisible where it isn't configured: exec the next matching binary on PATH AFTER our own entry
// (PassthroughScan) - never ourselves, and never a shims dir ahead of us, so a dev tack behind a release tack
// can't ping-pong with it.
static (string? Target, string? Error) Passthrough(string exposed)
{
    foreach (var entry in PassthroughScan.Candidates(Environment.GetEnvironmentVariable("PATH"), AppContext.BaseDirectory))
    {
        string? found = BinaryLocator.Locate(entry, exposed, File.Exists);
        if (found is not null)
        {
            Debug($"passthrough -> {found}");
            return (found, null);
        }
    }
    return (null, $"'{exposed}' did not resolve for this directory and no fallback was found on PATH");
}

// ---- invocation log -----------------------------------------------------------------------------

// Best effort by design: a debug log must never be the reason a tool call fails.
static void LogInvocation(string logPath, string exposed, string[] forwarded, Resolution res, string? target, string? error)
{
    try
    {
        var (callers, end) = Native.Callers(maxDepth: 8);
        var entry = new ShimLogEntry
        {
            Pid = Environment.ProcessId,
            Exposed = exposed,
            Args = forwarded,
            Cwd = Environment.CurrentDirectory,
            Source = res.Source.ToString(),
            Version = res.Version,
            Detail = res.Detail,
            Target = target,
            Error = error,
            Callers = callers,
            CallersEnd = end,
        };
        if (!ShimLog.Append(logPath, ShimLog.Format(entry)))
            Debug($"log entry dropped ({logPath} busy or unwritable)");
    }
    catch (Exception ex)
    {
        Debug($"log failed: {ex.Message}");
    }
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

    // The data dir the shim was stamped into: <root>\shims\node.exe -> <root>\resolved.json. This ties a shim to
    // ITS profile (tack vs tack (Dev)) by where it lives, not by how it was compiled - so a dev instance's shims
    // read the dev config even when the shim binary itself is a Release build.
    if (Directory.GetParent(AppContext.BaseDirectory.TrimEnd('\\', '/')) is { } root)
    {
        string owner = Path.Combine(root.FullName, "resolved.json");
        if (File.Exists(owner)) return owner;
    }

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

    // ---- caller chain (invocation log only) ----

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessBasicInformationClass = 0;

    // The shim's ancestors, parent first, as far as they can be followed. Windows only records a parent's PID,
    // and a PID is recycled once its process exits - so a "parent" created after its child is an impostor: the
    // real one has gone, and the walk stops there. Only query-limited access is asked for (what Task Manager
    // uses); an elevated or protected ancestor that refuses even that ends the walk too.
    public static (List<CallerProcess> Chain, string? End) Callers(int maxDepth)
    {
        var chain = new List<CallerProcess>();
        nint self = GetCurrentProcess();
        if (!TryGetParent(self, out int pid) || !GetProcessTimes(self, out long childCreated, out _, out _, out _))
            return (chain, "(parent unknown)");

        while (pid != 0)
        {
            if (chain.Count == maxDepth) return (chain, "...");

            nint h = OpenProcess(ProcessQueryLimitedInformation, inheritHandle: false, (uint)pid);
            if (h == 0)
            {
                chain.Add(new CallerProcess(pid, null));
                return (chain, "(can't look further: access denied - elevated or protected process?)");
            }
            try
            {
                if (!GetProcessTimes(h, out long created, out _, out _, out _) || created > childCreated)
                    return (chain, $"(parent [{pid}] has exited)");

                chain.Add(new CallerProcess(pid, ImagePath(h)));
                childCreated = created;
                if (!TryGetParent(h, out pid)) return (chain, null);
            }
            finally
            {
                CloseHandle(h);
            }
        }
        return (chain, null);
    }

    private static unsafe bool TryGetParent(nint process, out int parentPid)
    {
        parentPid = 0;
        if (NtQueryInformationProcess(process, ProcessBasicInformationClass, out var pbi,
                sizeof(ProcessBasicInformation), out _) != 0)
            return false;
        parentPid = (int)pbi.InheritedFromUniqueProcessId;
        return true;
    }

    private static unsafe string? ImagePath(nint process)
    {
        char* buffer = stackalloc char[1024];
        uint size = 1024;
        return QueryFullProcessImageName(process, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
    }

    // Every field is pointer-sized on both x86 and x64 (NTSTATUS and KPRIORITY are padded on x64).
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public nint ExitStatus;
        public nint PebBaseAddress;
        public nint AffinityMask;
        public nint BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(nint process, out long creationTime, out long exitTime,
        out long kernelTime, out long userTime);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(nint process, uint flags, char* exeName, ref uint size);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(nint process, int infoClass,
        out ProcessBasicInformation info, int length, out int returnLength);
}
