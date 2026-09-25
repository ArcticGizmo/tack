using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Tack.Core.Platform;

/// <summary>
/// Windows PATH installer. tack only ever edits the SYSTEM (machine) PATH - never the user PATH. On Windows the
/// effective PATH is machine entries then user entries, so a user-PATH shims dir loses to every system-wide tool
/// install; and tack must not scribble on the user's own PATH. The shims dir goes to the FRONT (it must win over
/// other tool installs like nvm-windows) and the Velopack install dir is appended so tack.exe resolves.
///
/// Every write goes through <see cref="WindowsEnvRegistry"/> on the raw value, so <c>%VAR%</c> tokens survive and
/// the value stays <c>REG_EXPAND_SZ</c>, then broadcasts WM_SETTINGCHANGE so new processes see it without a
/// logoff. Writing HKLM needs admin: unelevated, the writes throw (ERROR_ACCESS_DENIED) and the caller decides
/// whether to relaunch elevated through UAC. Reads (<see cref="NeedsRegister"/>) work unelevated.
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

    public string InstallDir => _installDir;
    public string ShimsDir => _shimsDir;

    /// <summary>True when the system PATH doesn't yet have the shims dir at the front and the install dir on it.</summary>
    public bool NeedsRegister() =>
        PathEdits.Register(WindowsEnvRegistry.ReadRaw(machine: true), _shimsDir, _installDir) is not null;

    /// <summary>True when the system PATH still holds the shims dir or install dir.</summary>
    public bool NeedsUnregister() =>
        PathEdits.Remove(WindowsEnvRegistry.ReadRaw(machine: true), new[] { _shimsDir, _installDir }) is not null;

    /// <summary>Wire the shims dir (front) and install dir onto the system PATH. Needs admin. Returns the
    /// before/after, or null if it was already in place and nothing was written.</summary>
    public PathChange? Register()
    {
        Directory.CreateDirectory(_shimsDir);
        return Write(before => PathEdits.Register(before, _shimsDir, _installDir));
    }

    /// <summary>Take tack's entries off the system PATH. Needs admin. Null if they weren't there.</summary>
    public PathChange? Unregister() =>
        Write(before => PathEdits.Remove(before, new[] { _shimsDir, _installDir }));

    /// <summary>
    /// Move the shims dir to the front of the system PATH - the core of <c>tack doctor --fix</c>. With
    /// <paramref name="behind"/> (a dev instance: the release shims dirs) it lands directly after the first of
    /// those on PATH instead of at index 0, so dev beats every real install but never the release tack.
    /// </summary>
    public PathChange? PromoteShimsOnMachinePath(IEnumerable<string>? behind = null) =>
        Write(before => PathEdits.PromoteFront(before, _shimsDir, behind));

    private static PathChange? Write(Func<string, string?> edit)
    {
        string before = WindowsEnvRegistry.ReadRaw(machine: true);
        string? after = edit(before);
        if (after is null) return null;

        WindowsEnvRegistry.WriteMachine(after);
        Broadcast();
        return new PathChange("machine", before, after);
    }

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
