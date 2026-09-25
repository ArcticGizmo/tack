using Tack.Core.Config;

namespace Tack.Core.Maintenance;

/// <summary>
/// The install/update wiring the Velopack hook runs (scope plan section 8): put tack's dirs on the system PATH
/// (when the caller can - that write needs admin, so an unelevated hook passes null and leaves PATH to the
/// first-run setup), then regenerate shims from whatever is already registered. The second step is what a bare
/// PATH register misses - after an update the shim binary itself may have changed (so existing copies are
/// stale), and after a reinstall a user who already had tools registered should get working shims without
/// running `tack reshim`. PATH first, so even if shim stamping fails (a copy locked mid-update) the essential
/// PATH wiring is done. Pure and injectable (IPathInstaller + ShimPayload) so it's testable without touching
/// the real registry.
/// </summary>
public static class FirstRun
{
    public static ReshimResult Apply(
        IPathInstaller? pathInstaller,
        CentralConfig config,
        string shimsDir,
        string resolvedJsonPath,
        ShimPayload payload)
    {
        pathInstaller?.Register();
        return Reshimmer.Run(config, shimsDir, resolvedJsonPath, payload);
    }
}
