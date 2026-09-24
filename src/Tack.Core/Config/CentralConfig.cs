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

    /// <summary>Central zones: a directory (and everything under it) resolves a tool without a repo tack.yml.
    /// One entry per (path, tool) - see <see cref="ZoneRegistry"/>.</summary>
    public List<Zone> Zones { get; set; } = new();

    /// <summary>Pre-zones glob bindings. <see cref="ZoneRegistry.Migrate"/> turns these into zones on load; any
    /// left here have a mid-path wildcard, can't be migrated, and are reported by doctor/reshim. Null once empty,
    /// so a clean config never writes the key.</summary>
    public List<LegacyBinding>? Bindings { get; set; }

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

/// <summary>A central rule: <see cref="Path"/> and every directory under it resolves <see cref="Tool"/> to
/// <see cref="Version"/>. The deepest zone containing the current directory wins.</summary>
public sealed class Zone
{
    /// <summary>An absolute directory, e.g. "C:\work\employer". Compared case- and separator-insensitively.</summary>
    public string Path { get; set; } = "";

    public string Tool { get; set; } = "";

    public string Version { get; set; } = "";

    /// <summary>When true, this zone beats a repo tack.yml (org enforcement). Default false.</summary>
    public bool Enforce { get; set; }
}

/// <summary>The pre-zones binding shape: a directory glob mapped to several tools. Read only for migration.</summary>
public sealed class LegacyBinding
{
    public string Glob { get; set; } = "";
    public Dictionary<string, string> Tools { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Enforce { get; set; }
}

public sealed class TackSettings
{
    /// <summary>What to do when nothing resolves: "passthrough" (default) or "error".</summary>
    public string NoResolution { get; set; } = "passthrough";
}
