using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Tack.Core.Platform;

/// <summary>
/// Reads and writes the environment PATH values in the registry <b>without</b> collapsing their environment
/// references. <c>Environment.GetEnvironmentVariable(.., Machine/User)</c> returns PATH already expanded
/// (<c>%SystemRoot%\system32</c> -> <c>C:\WINDOWS\system32</c>), and <c>SetEnvironmentVariable</c> writes it
/// back as a plain <c>REG_SZ</c> - so round-tripping PATH through them destroys the <c>%VAR%</c> tokens and
/// flips the value's type. That's fine for a throwaway lookup but wrong for editing the machine PATH, where
/// those tokens (and the <c>REG_EXPAND_SZ</c> type) matter. So <c>tack doctor --fix</c> goes to the registry
/// directly: read the raw value, edit the raw entries, write it back as <c>REG_EXPAND_SZ</c>. P/Invoke (not the
/// Microsoft.Win32.Registry package) keeps Tack.Core dependency-free for the NativeAOT shim.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsEnvRegistry
{
    private const string UserSubKey = @"Environment";
    private const string MachineSubKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    private static readonly IntPtr HKEY_CURRENT_USER = unchecked((IntPtr)(int)0x80000001);
    private static readonly IntPtr HKEY_LOCAL_MACHINE = unchecked((IntPtr)(int)0x80000002);

    private const int KEY_QUERY_VALUE = 0x0001;
    private const int KEY_SET_VALUE = 0x0002;

    private const int ERROR_SUCCESS = 0;
    private const int ERROR_FILE_NOT_FOUND = 2;
    private const int ERROR_MORE_DATA = 234;

    private const int REG_SZ = 1;
    private const int REG_EXPAND_SZ = 2;

    /// <summary>The raw, unexpanded PATH string for the given scope; "" if it isn't set or can't be read.</summary>
    public static string ReadRaw(bool machine, string valueName = "PATH")
    {
        var (root, sub) = machine ? (HKEY_LOCAL_MACHINE, MachineSubKey) : (HKEY_CURRENT_USER, UserSubKey);
        if (RegOpenKeyEx(root, sub, 0, KEY_QUERY_VALUE, out IntPtr hKey) != ERROR_SUCCESS)
            return "";
        try
        {
            int type = 0, cb = 0;
            int rc = RegQueryValueEx(hKey, valueName, IntPtr.Zero, ref type, null, ref cb);
            if (rc == ERROR_FILE_NOT_FOUND || cb <= 0) return "";
            if (rc != ERROR_SUCCESS && rc != ERROR_MORE_DATA) return "";

            var data = new byte[cb];
            rc = RegQueryValueEx(hKey, valueName, IntPtr.Zero, ref type, data, ref cb);
            if (rc != ERROR_SUCCESS) return "";
            if (type != REG_SZ && type != REG_EXPAND_SZ) return "";

            return Encoding.Unicode.GetString(data, 0, cb).TrimEnd('\0');
        }
        finally { RegCloseKey(hKey); }
    }

    /// <summary>Write the PATH string for the given scope as <c>REG_EXPAND_SZ</c> (preserving %VAR% tokens).
    /// Writing the machine scope needs admin - unelevated it throws with ERROR_ACCESS_DENIED.</summary>
    public static void WriteExpand(bool machine, string value, string valueName = "PATH")
    {
        var (root, sub) = machine ? (HKEY_LOCAL_MACHINE, MachineSubKey) : (HKEY_CURRENT_USER, UserSubKey);
        int rc = RegOpenKeyEx(root, sub, 0, KEY_SET_VALUE, out IntPtr hKey);
        if (rc != ERROR_SUCCESS)
            throw new Win32Exception(rc, $"opening {(machine ? "HKLM" : "HKCU")}\\{sub} for write failed");
        try
        {
            var bytes = Encoding.Unicode.GetBytes(value + '\0');
            rc = RegSetValueEx(hKey, valueName, 0, REG_EXPAND_SZ, bytes, bytes.Length);
            if (rc != ERROR_SUCCESS)
                throw new Win32Exception(rc, "writing the PATH value failed");
        }
        finally { RegCloseKey(hKey); }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegOpenKeyExW", SetLastError = true)]
    private static extern int RegOpenKeyEx(IntPtr hKey, string subKey, int options, int samDesired, out IntPtr result);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegQueryValueExW", SetLastError = true)]
    private static extern int RegQueryValueEx(IntPtr hKey, string valueName, IntPtr reserved, ref int type, byte[]? data, ref int cbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegSetValueExW", SetLastError = true)]
    private static extern int RegSetValueEx(IntPtr hKey, string valueName, int reserved, int type, byte[] data, int cbData);

    [DllImport("advapi32.dll")]
    private static extern int RegCloseKey(IntPtr hKey);
}
