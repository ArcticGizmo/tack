namespace Tack.Core.Resolution;

/// <summary>
/// Locates the real executable for an exposed name inside a version's binDir, probing the Windows exec
/// extensions (npm is npm.cmd, node is node.exe, etc.). File existence is injected so it stays testable.
/// </summary>
public static class BinaryLocator
{
    private static readonly string[] Extensions = { "", ".exe", ".cmd", ".bat" };

    public static string? Locate(string binDir, string exposedName, Func<string, bool> fileExists)
    {
        foreach (var ext in Extensions)
        {
            string candidate = Path.Combine(binDir, exposedName + ext);
            if (fileExists(candidate)) return candidate;
        }
        return null;
    }
}
