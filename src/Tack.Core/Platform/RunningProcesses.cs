using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Tack.Core.Platform;

/// <summary>
/// Which programs are running from a folder. Removing a managed version needs this because Windows lets a folder be
/// renamed while an exe in it is running (it only refuses to delete the exe), so the rename into <c>.trash</c> alone
/// doesn't catch a running node (found in M10 checkpoint 5). Only query-limited access is asked for, the same as
/// Task Manager; a process that refuses even that (another account's, a protected one) can't be one of yours anyway.
/// </summary>
[SupportedOSPlatform("windows")]
public static class RunningProcesses
{
    /// <summary>Every other process whose executable is inside <paramref name="folder"/>, as <c>node.exe (pid 1234)</c>.</summary>
    public static IReadOnlyList<string> From(string folder)
    {
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        var found = new List<string>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                if (ImagePath(process.Id) is { } path && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    found.Add($"{Path.GetFileName(path)} (pid {process.Id})");
            }
        }
        return found;
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    private static string? ImagePath(int pid)
    {
        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
