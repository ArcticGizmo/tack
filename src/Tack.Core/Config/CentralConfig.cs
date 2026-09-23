namespace Tack.Core.Config;

/// <summary>
/// The central, tool-managed config (config.json). Authored by the `tack` CLI / UI, never hand-edited in
/// the normal case. Compiled to <see cref="ResolvedConfig"/> (resolved.json) for the shim to read. See the
/// scope plan section 3.3.
/// </summary>
public sealed class CentralConfig
{
    /// <summary>Registry: tool name -> its installed versions.</summary>
    public Dictionary<string, RegisteredTool> Tools { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Central directory bindings (managed without a repo tack.yml).</summary>
    public List<Binding> Bindings { get; set; } = new();

    /// <summary>Global fallback versions: tool name -> version.</summary>
    public Dictionary<string, string> Defaults { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public TackSettings Settings { get; set; } = new();
}

public sealed class RegisteredTool
{
    /// <summary>Installed versions: version string -> where it lives + what it exposes.</summary>
    public Dictionary<string, InstalledVersion> Versions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class InstalledVersion
{
    /// <summary>Absolute directory holding the tool's executables.</summary>
    public string BinDir { get; set; } = "";

    /// <summary>Executable names this version provides (e.g. node exposes node, npm, npx, corepack).</summary>
    public List<string> Exposes { get; set; } = new();
}

/// <summary>A central rule: any directory matching <see cref="Glob"/> resolves the listed tools.</summary>
public sealed class Binding
{
    /// <summary>A directory glob (see <see cref="Tack.Core.Resolution.Glob"/>), e.g. "C:/work/employer/**".</summary>
    public string Glob { get; set; } = "";

    /// <summary>tool name -> version.</summary>
    public Dictionary<string, string> Tools { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When true, this binding beats a repo tack.yml (org enforcement). Default false.</summary>
    public bool Enforce { get; set; }
}

public sealed class TackSettings
{
    /// <summary>What to do when nothing resolves: "passthrough" (default) or "error".</summary>
    public string NoResolution { get; set; } = "passthrough";
}
