using Tack.Core.Config;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Tack.Cli;

/// <summary>
/// The full tack.yml parser used by the CLI for authoring and validation (YamlDotNet). The shim uses
/// Core's dependency-free <see cref="MiniTackYml"/> instead; a cross-check test asserts the two agree on the
/// supported grammar, so the shim's mini-parser can't silently drift from what authors write.
/// </summary>
public static class TackYmlParser
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance) // yaml `tools` -> Tools
        .IgnoreUnmatchedProperties()                              // v1 grammar is just the tools map
        .Build();

    public static TackYmlDocument Parse(string text)
    {
        var file = Deserializer.Deserialize<YmlFile?>(text) ?? new YmlFile();
        var tools = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (file.Tools is not null)
            foreach (var (k, v) in file.Tools)
                tools[k] = v ?? "";
        return new TackYmlDocument(tools);
    }

    private sealed class YmlFile
    {
        public Dictionary<string, string>? Tools { get; set; }
    }
}
