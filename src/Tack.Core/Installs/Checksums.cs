namespace Tack.Core.Installs;

/// <summary>SHA-256 values as vendors publish them.</summary>
public static class Checksums
{
    /// <summary>The value in lowercase if it's 64 hex digits, otherwise null.</summary>
    public static string? Sha256(string? value)
    {
        if (value is not { Length: 64 }) return null;
        foreach (char c in value)
            if (!char.IsAsciiHexDigit(c)) return null;
        return value.ToLowerInvariant();
    }

    /// <summary>
    /// The SHA-256 a <c>sha256sum</c>-format manifest (Node's <c>SHASUMS256.txt</c>) gives for <paramref name="fileName"/>:
    /// lines of <c>&lt;hash&gt;  &lt;name&gt;</c>, a <c>*</c> before the name for binary mode, either line ending. The
    /// name must match exactly. Null if it isn't listed, is listed twice with different values, or its value isn't a
    /// SHA-256.
    /// </summary>
    public static string? FromManifest(string manifest, string fileName)
    {
        string? found = null;
        foreach (var raw in manifest.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            int space = line.IndexOf(' ');
            if (space <= 0) continue;
            string name = line[(space + 1)..].TrimStart(' ').TrimStart('*');
            if (!string.Equals(name, fileName, StringComparison.Ordinal)) continue;
            string? hash = Sha256(line[..space]);
            if (hash is null || (found is not null && found != hash)) return null;
            found = hash;
        }
        return found;
    }
}
