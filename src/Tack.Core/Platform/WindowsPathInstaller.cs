using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Tack.Core.Platform;

/// <summary>
/// Windows PATH installer. tack only ever edits the USER PATH, and only its own entries; it never writes the system
/// PATH (ADR 0002). The shims dir goes to the FRONT of the user PATH, so it wins over other per-user installs
/// (scoop, the WindowsApps aliases); a dev instance goes just behind the release shims instead. The install dir, if
/// given, is appended so tack.exe resolves. Anything on the system PATH still comes first - that's what the
/// shadow report is for.
///
/// Every write goes through <see cref="WindowsEnvRegistry"/> on the raw value, so <c>%VAR%</c> tokens survive and
/// the value stays <c>REG_EXPAND_SZ</c>, then broadcasts WM_SETTINGCHANGE so new processes see it without a
/// logoff. No admin needed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPathInstaller : IPathInstaller
{
    private readonly string? _installDir;
    private readonly IReadOnlyList<string>? _behind;

    /// <param name="shimsDir">This profile's shims dir.</param>
    /// <param name="installDir">The dir holding tack.exe, appended so <c>tack</c> resolves; null to leave it off
    /// (a dev build runs from its build output).</param>
    /// <param name="behind">Dirs the shims sit directly behind, if they're on the PATH (a dev instance: the release
    /// shims); null for the very front.</param>
    public WindowsPathInstaller(string shimsDir, string? installDir, IReadOnlyList<string>? behind = null)
    {
        ShimsDir = shimsDir.TrimEnd('\\', '/');
        _installDir = installDir?.TrimEnd('\\', '/');
        _behind = behind;
    }

    public string ShimsDir { get; }

    private IEnumerable<string> Ours => _installDir is null ? new[] { ShimsDir } : new[] { ShimsDir, _installDir };

    /// <summary>True when the user PATH doesn't yet have the shims dir in place and the install dir on it.</summary>
    public bool NeedsRegister() => Edit(Raw()) is not null;

    /// <summary>True when the user PATH still holds the shims dir or install dir.</summary>
    public bool NeedsUnregister() => PathEdits.Remove(Raw(), Ours) is not null;

    /// <summary>Put the shims dir in place on the user PATH (moving it up if something got ahead of it) and the
    /// install dir on it. Returns the before/after, or null if it was already in place and nothing was written.</summary>
    public PathChange? Register()
    {
        Directory.CreateDirectory(ShimsDir);
        return Write(Edit);
    }

    /// <summary>Take tack's entries off the user PATH. Null if they weren't there.</summary>
    public PathChange? Unregister() => Write(before => PathEdits.Remove(before, Ours));

    private string? Edit(string before) => PathEdits.Register(before, ShimsDir, _installDir, _behind);

    private static string Raw() => WindowsEnvRegistry.ReadRaw(machine: false);

    private static PathChange? Write(Func<string, string?> edit)
    {
        string before = Raw();
        string? after = edit(before);
        if (after is null) return null;

        WindowsEnvRegistry.WriteUser(after);
        Broadcast();
        return new PathChange("user", before, after);
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
