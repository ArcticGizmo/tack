using Tack.Core.Maintenance;
using Tack.Core.Platform;

namespace Tack.App.Services;

/// <summary>
/// The Velopack install lifecycle, wired to the real machine. tack-ui is the Velopack mainExe, so it owns
/// these callbacks; they run in the installed app dir (so tack-shim.exe is co-located for stamping). Kept
/// out of Program.cs so the composition is named and the shim-regen step is easy to follow.
/// </summary>
internal static class InstallHook
{
    /// <summary>On install/update: wire PATH, then (re)stamp shims from existing config with this build's
    /// shim binary. Shim stamping is best-effort - a copy locked by a running tool mid-update must not fail
    /// the install; PATH is wired first, and `tack doctor`/`tack reshim` recover any missed shims.</summary>
    public static void Apply()
    {
        var services = new TackServices();
        try
        {
            FirstRun.Apply(new WindowsPathInstaller(), services.Load(),
                services.ShimsDir, services.ResolvedJson, services.ShimPayload());
        }
        catch
        {
            // Fall back to at least wiring PATH, so the shims dir is on PATH even if stamping threw.
            try { new WindowsPathInstaller().Register(); } catch { /* nothing more we can safely do here */ }
        }
    }

    /// <summary>On uninstall: strip tack's PATH entries. User config + shims under %LOCALAPPDATA%\tack are
    /// left in place, so a later reinstall keeps the registry/bindings the user built up.</summary>
    public static void Remove()
    {
        try { new WindowsPathInstaller().Unregister(); } catch { /* best-effort */ }
    }
}
