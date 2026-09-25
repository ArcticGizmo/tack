namespace Tack.Core.Maintenance;

/// <summary>
/// Auto-detects the executables a version's binDir provides (for `tack tool add` without an explicit
/// --exposes). Scans for the Windows exec extensions and returns distinct basenames.
/// </summary>
public static class ToolProbe
{
    private static readonly string[] Extensions = { ".exe", ".cmd", ".bat" };

    public static List<string> DetectExposes(string binDir)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(binDir))
        {
            foreach (var ext in Extensions)
                foreach (var file in Directory.GetFiles(binDir, "*" + ext))
                    names.Add(Path.GetFileNameWithoutExtension(file));
        }
        return names.ToList();
    }
}
