namespace Tack.Core.Config;

/// <summary>The parsed contents of a project tack.yml. v1 is deliberately just a flat tool -> version map.</summary>
public sealed class TackYmlDocument
{
    public Dictionary<string, string> Tools { get; }

    public TackYmlDocument(Dictionary<string, string> tools) => Tools = tools;
}
