using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Tack.Core.Platform;

/// <summary>
/// Windows PATH installer: edits the USER PATH (HKCU\Environment, via the .NET User target) and broadcasts
/// WM_SETTINGCHANGE so freshly launched processes pick it up without a logoff - no admin needed. Ported
/// from perch's PathInstaller, adapted for tack: the shims dir is PREPENDED (it must win over other tool
/// installs like nvm-windows), and the Velopack install dir is appended so tack.exe/tack-ui.exe resolve.
///
/// Full PATH-ordering hardening vs other managers (and the `tack doctor` diagnosis) is M3; this M0 version
/// just gets the two dirs on PATH idempotently.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPathInstaller : IPathInstaller
{
    private readonly string _installDir;
    private readonly string _shimsDir;

    public WindowsPathInstaller(string? installDir = null, string? shimsDir = null)
    {
        _installDir = (installDir ?? AppContext.BaseDirectory).TrimEnd('\\', '/');
        _shimsDir = (shimsDir ?? TackPaths.ShimsDir).TrimEnd('\\', '/');
    }

    public void Register()
    {
        Directory.CreateDirectory(_shimsDir);

        var entries = Split(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "");
        bool changed = false;

        // The shims dir must precede other tool installs, so prepend it (if not already present).
        if (!entries.Any(p => PathEquals(p, _shimsDir)))
        {
            entries.Insert(0, _shimsDir);
            changed = true;
        }
        // The install dir only needs to be resolvable, so append it.
        if (!entries.Any(p => PathEquals(p, _installDir)))
        {
            entries.Add(_installDir);
            changed = true;
        }

        if (!changed) return;
        Environment.SetEnvironmentVariable("PATH", string.Join(';', entries), EnvironmentVariableTarget.User);
        Broadcast();
    }

    public void Unregister()
    {
        var current = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User);
        if (string.IsNullOrEmpty(current)) return;

        var kept = Split(current).Where(p => !PathEquals(p, _shimsDir) && !PathEquals(p, _installDir)).ToList();
        Environment.SetEnvironmentVariable("PATH", string.Join(';', kept), EnvironmentVariableTarget.User);
        Broadcast();
    }

    /// <summary>
    /// Move the shims dir to the very front of the MACHINE PATH so it beats system-wide tool installs a
    /// user-PATH entry can't - the core of <c>tack doctor --fix</c>. Reads and writes the raw registry value so
    /// %VAR% tokens survive, and returns the before/after (null when it already led and nothing was written).
    /// Writing HKLM needs admin: unelevated it throws (ERROR_ACCESS_DENIED), which the caller turns into a UAC
    /// relaunch.
    /// </summary>
    public PathChange? PrependShimsToMachinePath()
    {
        string before = WindowsEnvRegistry.ReadRaw(machine: true);
        string? after = PathEdits.PrependFront(before, _shimsDir);
        if (after is null) return null;

        WindowsEnvRegistry.WriteExpand(machine: true, after);
        Broadcast();
        return new PathChange("machine", before, after);
    }

    private static List<string> Split(string pathVar) =>
        pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static bool PathEquals(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private const int HWND_BROADCAST = 0xffff;
    private const int WM_SETTINGCHANGE = 0x1A;
    private const int SMTO_ABORTIFHUNG = 0x2;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, int Msg, IntPtr wParam, string lParam, int fuFlags, int uTimeout, out IntPtr lpdwResult);

    private static void Broadcast()
    {
        try
        {
            SendMessageTimeout((IntPtr)HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, "Environment",
                SMTO_ABORTIFHUNG, 5000, out _);
        }
        catch { /* best-effort: a failed broadcast just means new shells need a relog to see the change */ }
    }
}
