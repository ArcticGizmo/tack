using System.Runtime.Versioning;
using Tack.Core.Maintenance;
using Tack.Core.Platform;
using Velopack;

namespace Tack.Cli;

/// <summary>
/// The Velopack install lifecycle, wired to the real machine. tack.exe is the Velopack mainExe, so it owns
/// these callbacks; they run in the installed app dir (so tack-shim.exe is co-located for stamping). Kept
/// out of Program.cs so the composition is named and the shim-regen step is easy to follow.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class InstallHook
{
    /// <summary>Registers the install/update/uninstall callbacks on the Velopack bootstrap.</summary>
    public static VelopackApp Wire(VelopackApp app) => app
        .OnAfterInstallFastCallback(_ => Apply())
        .OnAfterUpdateFastCallback(_ => Apply())
        .OnBeforeUninstallFastCallback(_ => Remove());

    /// <summary>On install/update: wire PATH, then (re)stamp shims from existing config with this build's
    /// shim binary. Shim stamping is best-effort - a copy locked by a running tool mid-update must not fail
    /// the install; PATH is wired first, and `tack doctor`/`tack reshim` recover any missed shims. Stamps into
    /// the ACTIVE shims dir, so an update while `tack disable`d stays disabled.</summary>
    public static void Apply()
    {
        var env = new TackEnvironment();
        try
        {
            FirstRun.Apply(new WindowsPathInstaller(), env.Load(),
                env.ActiveShimsDir, env.ResolvedJson, env.ShimPayload());
        }
        catch
        {
            // Fall back to at least wiring PATH, so the shims dir is on PATH even if stamping threw.
            try { new WindowsPathInstaller().Register(); } catch { /* nothing more we can safely do here */ }
        }
    }

    /// <summary>On uninstall: strip tack's PATH entries. User config + shims under %LOCALAPPDATA%\tack are
    /// left in place, so a later reinstall keeps the registry/zones the user built up.</summary>
    public static void Remove()
    {
        try { new WindowsPathInstaller().Unregister(); } catch { /* best-effort */ }
    }
}
