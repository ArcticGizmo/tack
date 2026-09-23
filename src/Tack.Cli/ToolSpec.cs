namespace Tack.Cli;

/// <summary>Parses a "tool" or "tool@version" argument.</summary>
public readonly record struct ToolSpec(string Tool, string? Version)
{
    public static ToolSpec Parse(string s)
    {
        int at = s.IndexOf('@');
        return at < 0
            ? new ToolSpec(s.Trim(), null)
            : new ToolSpec(s[..at].Trim(), s[(at + 1)..].Trim());
    }
}
