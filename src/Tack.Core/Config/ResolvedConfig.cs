using System.Text.Json.Serialization;

namespace Tack.Core.Config;

/// <summary>
/// The compiled, shim-facing config (resolved.json). A flat, fast-lookup form of <see cref="CentralConfig"/>:
/// zones are split per tool with a precomputed match key, and every exposed binary name is mapped
/// to its owning tool. The shim reads this with the System.Text.Json source generator (AOT-safe).
/// </summary>
public sealed class ResolvedConfig
{
    /// <summary>exposed binary name -> owning tool (e.g. "npm" -> "node"). Includes each tool's own name.</summary>
    public Dictionary<string, string> Index { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, ResolvedTool> Tools { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public TackSettings Settings { get; set; } = new();
}

public sealed class ResolvedTool
{
    /// <summary>Central default version for this tool, if any.</summary>
    public string? Default { get; set; }

    public Dictionary<string, ResolvedVersion> Versions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>This tool's central zones.</summary>
    public List<ResolvedZone> Zones { get; set; } = new();
}

public sealed class ResolvedVersion
{
    public string BinDir { get; set; } = "";
    public List<string> Exposes { get; set; } = new();
}

public sealed class ResolvedZone
{
    /// <summary>The zone's directory as the user gave it (for "why this version" messages).</summary>
    public string Path { get; set; } = "";

    /// <summary><see cref="Path"/> normalized by <see cref="Tack.Core.Resolution.ZonePath.Normalize"/>; the
    /// resolver compares the current directory's ancestors against this.</summary>
    public string Key { get; set; } = "";

    public string Version { get; set; } = "";
    public bool Enforce { get; set; }
}

/// <summary>
/// One source-gen context for both config shapes. AOT-safe: the shim reads <see cref="ResolvedConfig"/>
/// without reflection; the CLI reads and writes <see cref="CentralConfig"/> through the same generator.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ResolvedConfig))]
[JsonSerializable(typeof(CentralConfig))]
public partial class TackJson : System.Text.Json.Serialization.JsonSerializerContext { }
