namespace Tack.Core.Maintenance;

public enum GateAction { Disabled, Enabled, AlreadyDisabled, AlreadyEnabled, Conflict }

public sealed record GateResult(GateAction Action, string Detail);

/// <summary>
/// Turns tack's interception on and off without touching PATH. The PATH entry always points at the canonical
/// shims dir; disabling just renames that dir to <c>shims_disabled</c>, so the PATH entry resolves to nothing
/// and tools fall through to the real PATH - an instant, admin-free "stop working" that even open shells see
/// (executable resolution hits the filesystem live). Enabling renames it back.
///
/// Crucially, config keeps working while disabled: <see cref="ActiveDir"/> returns the parked dir, so
/// <c>register</c> / <c>bind</c> / <c>reshim</c> stamp shims into <c>shims_disabled</c>, and <c>tack enable</c>
/// makes everything configured meanwhile go live in one rename. Uses the real filesystem directly (like
/// Reshimmer) and is exercised through temp dirs in tests.
/// </summary>
public static class ShimGate
{
    /// <summary>True when tack is disabled (the parked dir exists).</summary>
    public static bool IsDisabled(string disabledDir) => Directory.Exists(disabledDir);

    /// <summary>The dir writes should target right now: the parked dir while disabled, else the canonical dir.</summary>
    public static string ActiveDir(string shimsDir, string disabledDir) =>
        Directory.Exists(disabledDir) ? disabledDir : shimsDir;

    /// <summary>Rename the shims dir out of the way so nothing resolves through it. Idempotent.</summary>
    public static GateResult Disable(string shimsDir, string disabledDir)
    {
        if (Directory.Exists(disabledDir))
            return new GateResult(GateAction.AlreadyDisabled, disabledDir);

        if (Directory.Exists(shimsDir))
            Directory.Move(shimsDir, disabledDir);
        else
            // Nothing to park yet (never reshimmed): create the marker so state is unambiguously disabled
            // and any later reshim lands in the parked dir.
            Directory.CreateDirectory(disabledDir);

        return new GateResult(GateAction.Disabled, disabledDir);
    }

    /// <summary>Rename the parked dir back so PATH resolves through it again. Idempotent.</summary>
    public static GateResult Enable(string shimsDir, string disabledDir)
    {
        if (!Directory.Exists(disabledDir))
            return new GateResult(GateAction.AlreadyEnabled, shimsDir);

        if (Directory.Exists(shimsDir))
        {
            // An installer/update may have recreated an empty shims dir while we were disabled. If it's empty
            // it's safe to drop and put the real one back; if it holds shims we refuse rather than clobber.
            if (Directory.EnumerateFileSystemEntries(shimsDir).Any())
                return new GateResult(GateAction.Conflict, shimsDir);
            Directory.Delete(shimsDir);
        }

        Directory.Move(disabledDir, shimsDir);
        return new GateResult(GateAction.Enabled, shimsDir);
    }
}
