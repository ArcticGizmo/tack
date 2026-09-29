using Tack.Core.Config;

namespace Tack.Core.Maintenance;

public sealed record FirstRunResult(CompileResult Compiled, StampResult? Stamped);

/// <summary>
/// The install/update wiring the Velopack hook runs (scope plan section 8): put tack's dirs on the system PATH,
/// compile the installing user's config, and stamp the shims it needs. The PATH and shims steps write
/// admin-owned places, so an unelevated hook passes no installer and <c>stamp: false</c>, and leaves them to
/// <c>tack setup</c>. Stamping is what a bare PATH register misses: after an update the shim binary itself may have
/// changed (so existing copies are stale), and after a reinstall a user who already had tools registered should
/// get working shims straight away. PATH first, so even if stamping fails (a copy locked mid-update) the essential
/// PATH wiring is done. Pure and injectable (IPathInstaller + ShimPayload) so it's testable without touching the
/// real registry.
/// </summary>
public static class FirstRun
{
    public static FirstRunResult Apply(
        IPathInstaller? pathInstaller,
        CentralConfig config,
        string shimsDir,
        string resolvedJsonPath,
        ShimPayload payload,
        bool stamp)
    {
        pathInstaller?.Register();
        var compiled = Reshimmer.Compile(config, resolvedJsonPath);
        var stamped = stamp ? ShimStamper.Stamp(compiled.ShimNames, shimsDir, payload) : null;
        return new FirstRunResult(compiled, stamped);
    }
}
