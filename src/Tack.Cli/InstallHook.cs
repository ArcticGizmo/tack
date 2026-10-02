using System.Runtime.Versioning;
using Tack.Core.Maintenance;
using Velopack;

namespace Tack.Cli;

/// <summary>
/// The Velopack install lifecycle, wired to the real machine. tack.exe is the Velopack mainExe, so it owns
/// these callbacks; they run in the installed app dir (so tack-shim.exe is co-located for stamping). Kept
/// out of Program.cs so the composition is named and the shim-regen step is easy to follow.
///
/// PATH: tack only ever writes the USER PATH, and only its own entries; it never touches the system PATH (ADR
/// 0002). That needs no admin and takes milliseconds, so the hooks do it themselves, well inside the 15-30 s
/// Velopack gives fast callbacks. <c>tack setup</c> repeats it by hand if anything went wrong.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class InstallHook
{
    /// <summary>Registers the install/update/uninstall callbacks on the Velopack bootstrap.</summary>
    public static VelopackApp Wire(VelopackApp app) => app
        .OnAfterInstallFastCallback(_ => Apply())
        .OnAfterUpdateFastCallback(_ => Apply())
        .OnBeforeUninstallFastCallback(_ => Remove());

    /// <summary>On install/update: put tack on the user PATH, compile the user's config, and (re)stamp its shims with
    /// this build's shim binary. Stamping is best-effort - a copy locked by a running tool mid-update must not fail
    /// the install; `tack setup`/`tack doctor --fix` recover any missed shims. The install dir (...\current) is
    /// stable across versions, so an update only moves the PATH if something got ahead of the shims.</summary>
    public static void Apply()
    {
        var env = new TackEnvironment();
        var path = env.PathInstaller();
        try
        {
            FirstRun.Apply(new BackedUp(path), env.Load(), env.ShimsDir, env.ResolvedJson, env.ShimPayload());
        }
        catch
        {
            // Fall back to at least wiring PATH, so the shims dir is on PATH even if stamping threw.
            try { UserPath.Edit(path.Register); } catch { /* nothing more we can safely do here */ }
        }
    }

    /// <summary>On uninstall: take tack's entries off the user PATH. User config under %LOCALAPPDATA%\tack is left
    /// in place, so a later reinstall keeps the registry/zones the user built up.</summary>
    public static void Remove()
    {
        try { UserPath.Edit(new TackEnvironment().PathInstaller().Unregister); }
        catch { /* best-effort */ }
    }

    // The hooks have no console to report to, but every PATH edit still gets its backup.
    private sealed class BackedUp(Tack.Core.IPathInstaller inner) : Tack.Core.IPathInstaller
    {
        public Tack.Core.Platform.PathChange? Register() => UserPath.Edit(inner.Register).Change;
        public Tack.Core.Platform.PathChange? Unregister() => UserPath.Edit(inner.Unregister).Change;
    }
}
