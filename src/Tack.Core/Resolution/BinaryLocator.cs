namespace Tack.Core.Resolution;

/// <summary>
/// Locates the real executable for an exposed name inside a version's binDir, probing the Windows exec
/// extensions (npm is npm.cmd, node is node.exe, etc.). File existence is injected so it stays testable.
///
/// <para>A bare, extensionless file is never a match: node ships <c>npx</c> (a POSIX shell script for Git Bash)
/// right beside <c>npx.cmd</c>, and CreateProcess can't launch the former. A name that already carries an exec
/// extension (<c>node.exe</c>) is tried as-is.</para>
/// </summary>
public static class BinaryLocator
{
    private static readonly string[] Extensions = { ".exe", ".cmd", ".bat" };

    public static string? Locate(string binDir, string exposedName, Func<string, bool> fileExists)
    {
        if (Extensions.Contains(Path.GetExtension(exposedName), StringComparer.OrdinalIgnoreCase))
        {
            string exact = Path.Combine(binDir, exposedName);
            return fileExists(exact) ? exact : null;
        }

        foreach (var ext in Extensions)
        {
            string candidate = Path.Combine(binDir, exposedName + ext);
            if (fileExists(candidate)) return candidate;
        }
        return null;
    }
}
