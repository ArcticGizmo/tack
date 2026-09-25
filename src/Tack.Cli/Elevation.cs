using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Tack.Cli;

/// <summary>
/// Admin detection and a UAC relaunch, used (via <see cref="SystemPath"/>) for the one write that needs
/// elevation: the system PATH. We deliberately relaunch just the tiny machine-PATH step elevated rather than
/// run all of tack as admin.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Elevation
{
    public static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public enum RelaunchOutcome { Succeeded, Failed, Cancelled }

    /// <summary>Relaunch this exe elevated with the given args, wait for it, and report how it went.
    /// A UAC decline surfaces as <see cref="RelaunchOutcome.Cancelled"/> rather than an exception. With a
    /// <paramref name="timeout"/>, a child still running when it expires counts as failed (and is left to finish).</summary>
    public static RelaunchOutcome RelaunchElevated(string arguments, TimeSpan? timeout = null)
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return RelaunchOutcome.Failed;

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = arguments,
            UseShellExecute = true, // required for the runas verb
            Verb = "runas",
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return RelaunchOutcome.Failed;
            if (timeout is { } t) { if (!proc.WaitForExit(t)) return RelaunchOutcome.Failed; }
            else proc.WaitForExit();
            return proc.ExitCode == 0 ? RelaunchOutcome.Succeeded : RelaunchOutcome.Failed;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED - user declined the UAC prompt
        {
            return RelaunchOutcome.Cancelled;
        }
        catch { return RelaunchOutcome.Failed; }
    }
}
