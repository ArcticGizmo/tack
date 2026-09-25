using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Spectre.Console;
using Tack.Cli.Commands;
using Tack.Core;
using Tack.Core.Maintenance;
using Tack.Core.Platform;
using Velopack;

namespace Tack.Cli;

/// <summary>
/// The Velopack install lifecycle, wired to the real machine. tack.exe is the Velopack mainExe, so it owns
/// these callbacks; they run in the installed app dir (so tack-shim.exe is co-located for stamping). Kept
/// out of Program.cs so the composition is named and the shim-regen step is easy to follow.
///
/// PATH: tack only ever writes the SYSTEM PATH, never the user PATH. That write needs admin, and Velopack's
/// fast callbacks are killed after 15-30 s - far too short to sit on a UAC prompt - so the install/update hooks
/// only wire PATH when they happen to be elevated already. Otherwise the first run after Setup does it
/// (<see cref="FirstTimeSetup"/>, one UAC prompt), and <c>tack setup</c> retries it by hand.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class InstallHook
{
    /// <summary>Set when Velopack reports this is the first launch after an install.</summary>
    public static bool IsFirstRun { get; private set; }

    /// <summary>Registers the install/update/uninstall/first-run callbacks on the Velopack bootstrap.</summary>
    public static VelopackApp Wire(VelopackApp app) => app
        .OnAfterInstallFastCallback(_ => Apply())
        .OnAfterUpdateFastCallback(_ => Apply())
        .OnBeforeUninstallFastCallback(_ => Remove())
        .OnFirstRun(_ => IsFirstRun = true);

    /// <summary>On install/update: wire the system PATH if already elevated (never prompt here, never touch the
    /// user PATH), then (re)stamp shims from existing config with this build's shim binary. Shim stamping is
    /// best-effort - a copy locked by a running tool mid-update must not fail the install; `tack doctor`/`tack
    /// reshim` recover any missed shims. Stamps into the ACTIVE shims dir, so an update while `tack disable`d
    /// stays disabled. An update needs no PATH change: the install dir (...\current) is stable across versions.</summary>
    public static void Apply()
    {
        var env = new TackEnvironment();
        IPathInstaller? path = Elevation.IsAdministrator() ? new WindowsPathInstaller(shimsDir: env.ShimsDir) : null;
        try
        {
            FirstRun.Apply(path, env.Load(), env.ActiveShimsDir, env.ResolvedJson, env.ShimPayload());
        }
        catch
        {
            // Fall back to at least wiring PATH, so the shims dir is on PATH even if stamping threw.
            try { path?.Register(); } catch { /* nothing more we can safely do here */ }
        }
    }

    /// <summary>
    /// The first launch after Setup: put tack on the system PATH, prompting for UAC. Skipped when install.ps1 is
    /// driving the install (it runs <c>tack setup</c> itself, in the user's own terminal, after Setup exits).
    /// When this process owns its console window (Setup launched it), wait for Enter so the result is readable.
    /// </summary>
    public static int FirstTimeSetup()
    {
        if (Environment.GetEnvironmentVariable(SkipFirstRunVar) == "1") return 0;

        AnsiConsole.MarkupLine("[bold]tack first-time setup[/]");
        int rc;
        try { rc = PathSetup.Run(); }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]setup failed:[/] {Markup.Escape(ex.Message)}");
            rc = 1;
        }

        if (OwnsConsoleWindow() && !Console.IsInputRedirected)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[grey]press Enter to close.[/]");
            Console.ReadLine();
        }
        return rc;
    }

    /// <summary>Set by install.ps1 around the Setup run; see <see cref="FirstTimeSetup"/>.</summary>
    private const string SkipFirstRunVar = "TACK_SKIP_FIRSTRUN";

    /// <summary>On uninstall: strip tack's entries from the system PATH (one UAC prompt if not elevated, bounded
    /// to stay inside Velopack's 30 s limit). The user PATH is never touched. User config + shims under
    /// %LOCALAPPDATA%\tack are left in place, so a later reinstall keeps the registry/zones the user built up.</summary>
    public static void Remove()
    {
        try
        {
            var installer = new WindowsPathInstaller(shimsDir: new TackEnvironment().ShimsDir);
            if (!installer.NeedsUnregister()) return; // nothing on the system PATH: don't raise a UAC prompt for nothing
            SystemPath.Edit(() => installer.Unregister(),
                $"--remove --shims {SystemPath.Quote(installer.ShimsDir)} --install-dir {SystemPath.Quote(installer.InstallDir)}",
                timeout: TimeSpan.FromSeconds(25));
        }
        catch { /* best-effort */ }
    }

    private static bool OwnsConsoleWindow()
    {
        try { return GetConsoleProcessList(new uint[2], 2) == 1; }
        catch { return false; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList(uint[] processList, uint processCount);
}
