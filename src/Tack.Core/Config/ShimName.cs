using System.Text.RegularExpressions;

namespace Tack.Core.Config;

/// <summary>
/// The command names tack may stamp a shim for. A shim is written as <c>&lt;name&gt;.exe</c> into the admin-owned
/// shims dir by an elevated process, and the name reaches that process on its command line, so only a plain file
/// name gets through: a letter or digit, then letters, digits and <c>. _ + -</c>. That rules out separators,
/// <c>..</c>, quotes, spaces and wildcards. Windows device names (<c>con</c>, <c>nul</c>...) and tack's own
/// executables are refused too.
/// </summary>
public static partial class ShimName
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
        "tack", "tack-shim",
    };

    /// <summary>Why <paramref name="name"/> can't be a shim, or null if it can.</summary>
    public static string? Problem(string name)
    {
        if (!Allowed().IsMatch(name))
            return "only letters, digits and . _ + - are allowed, starting with a letter or digit";
        if (Reserved.Contains(name))
            return "reserved name";
        return null;
    }

    public static bool IsValid(string name) => Problem(name) is null;

    /// <summary>Every valid name exposed by any version of any tool: the shims this config needs.</summary>
    public static SortedSet<string> Exposed(CentralConfig config)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in config.Tools.Values)
            foreach (var version in tool.Versions.Values)
                foreach (var exposed in version.Exposes)
                    if (IsValid(exposed))
                        names.Add(exposed);
        return names;
    }

    /// <summary>Exposed names that can't be shims (a hand-edited config.json), as <c>tool@version: 'name' (why)</c>.
    /// They're ignored, and reported wherever the config is compiled or checked.</summary>
    public static List<string> Invalid(CentralConfig config)
    {
        var bad = new List<string>();
        foreach (var (toolName, tool) in config.Tools)
            foreach (var (version, iv) in tool.Versions)
                foreach (var exposed in iv.Exposes)
                    if (Problem(exposed) is { } why)
                        bad.Add($"{toolName}@{version}: '{exposed}' ({why})");
        return bad;
    }

    // \z, not $: $ also matches before a trailing newline.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+-]*\z")]
    private static partial Regex Allowed();
}
