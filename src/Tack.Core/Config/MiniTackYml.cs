namespace Tack.Core.Config;

/// <summary>
/// A tiny, dependency-free parser for the strict tack.yml subset - the exact code the NativeAOT shim runs
/// on the hot path (no YamlDotNet, which is AOT-hostile). It understands only a flat `tools:` block:
///
///   tools:
///     node: 20.11.0
///     python: "3.12"
///
/// A cross-check test (tests/Tack.Tests) feeds the same files to this and to the CLI's full YamlDotNet
/// parser and asserts they agree, so the shim's mini-parser can't silently drift from the schema.
/// </summary>
public static class MiniTackYml
{
    public static TackYmlDocument Parse(string text)
    {
        var tools = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool inTools = false;
        int toolsIndent = -1;

        foreach (var rawLine in text.Split('\n'))
        {
            string line = StripComment(rawLine.TrimEnd('\r'));
            if (string.IsNullOrWhiteSpace(line)) continue;

            int indent = LeadingSpaces(line);
            string trimmed = line.Trim();

            if (!inTools)
            {
                // Enter the block on a bare `tools:` key. (Anything else at the top level is ignored - the
                // v1 grammar is only the tools map; richer content lives in central JSON.)
                if (trimmed == "tools:" || trimmed.StartsWith("tools:", StringComparison.Ordinal)
                    && trimmed["tools:".Length..].Trim().Length == 0)
                {
                    inTools = true;
                    toolsIndent = indent;
                }
                continue;
            }

            // Inside the tools block until a line dedents to the block key's level (or shallower).
            if (indent <= toolsIndent)
            {
                inTools = false;
                continue;
            }

            int colon = trimmed.IndexOf(':');
            if (colon <= 0) continue;

            string key = trimmed[..colon].Trim();
            string value = Unquote(trimmed[(colon + 1)..].Trim());
            if (key.Length > 0) tools[key] = value;
        }

        return new TackYmlDocument(tools);
    }

    private static int LeadingSpaces(string s)
    {
        int i = 0;
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
        return i;
    }

    // A whole-line comment, or an inline " #..." (YAML requires whitespace before an inline comment).
    // Values in this grammar are version strings with no '#', so we don't need in-quote awareness.
    private static string StripComment(string line)
    {
        string t = line.TrimStart();
        if (t.StartsWith('#')) return "";
        int hash = line.IndexOf(" #", StringComparison.Ordinal);
        return hash >= 0 ? line[..hash] : line;
    }

    private static string Unquote(string v)
    {
        if (v.Length >= 2 && ((v[0] == '"' && v[^1] == '"') || (v[0] == '\'' && v[^1] == '\'')))
            return v[1..^1];
        return v;
    }
}
