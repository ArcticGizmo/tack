using System.Text.Json.Serialization;

namespace Tack.Core.Config;

/// <summary>
/// The central, tool-managed config (config.json). Authored by the `tack` CLI, never hand-edited in
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

    /// <summary>Environment variables set for this version's process (see <see cref="VersionEnv"/>). Null when
    /// there are none, so a plain version never writes the key.</summary>
    public Dictionary<string, string>? Env { get; set; }

    /// <summary>More absolute directories holding this version's executables, searched after <see cref="BinDir"/>
    /// (Python's <c>Scripts\</c>, where <c>pip.exe</c> lives). Null when there are none.</summary>
    public List<string>? ExtraBinDirs { get; set; }

    /// <summary>Where tack downloaded this version from, when <c>tack tool install</c> put it there. Null for a
    /// version registered with <c>tack tool add</c>. tack only ever deletes the files of a version that has one.</summary>
    public InstallReceipt? Install { get; set; }

    /// <summary><see cref="BinDir"/>, then each of <see cref="ExtraBinDirs"/>: where this version's commands are looked for.</summary>
    public IEnumerable<string> BinDirs() => ExtraBinDirs is { } extra ? extra.Prepend(BinDir) : new[] { BinDir };
}

/// <summary>A managed version's provenance (<c>tack tool install</c>), recorded when it was installed.</summary>
public sealed class InstallReceipt
{
    /// <summary>The source that resolved it, e.g. "nodejs.org" or "python.org".</summary>
    public string Source { get; set; } = "";

    /// <summary>The archive it was unpacked from.</summary>
    public string Url { get; set; } = "";

    /// <summary>The archive's SHA-256, as published by the vendor and checked before unpacking (lowercase hex).</summary>
    public string Sha256 { get; set; } = "";

    public DateTimeOffset InstalledAt { get; set; }
}

/// <summary>A central rule: <see cref="Path"/> and every directory under it resolves <see cref="Tool"/> to
/// <see cref="Version"/>. The deepest zone containing the current directory wins.</summary>
public sealed class Zone
{
    /// <summary>An absolute directory, e.g. "C:\work\employer". Compared case- and separator-insensitively.</summary>
    public string Path { get; set; } = "";

    /// <summary>A registered tool name, or <see cref="ZoneTool.All"/> for a zone that applies to every tool.</summary>
    public string Tool { get; set; } = "";

    /// <summary>A version (or prefix), or <see cref="ZoneVersion.None"/> to turn tack off for the tool here.</summary>
    public string Version { get; set; } = "";

    /// <summary>When true, this zone beats a repo tack.yml. Default false.</summary>
    public bool IgnoreTackFiles { get; set; }
}

/// <summary>
/// The reserved zone version <c>none</c>: "tack doesn't resolve this tool here". It's still a zone - deepest
/// wins, so it switches off an ancestor zone (and the central default) for its subtree, and a deeper zone can
/// switch tack back on below it. Where it wins, the shim hands the command to the next one on PATH.
/// </summary>
public static class ZoneVersion
{
    public const string None = "none";

    public static bool IsNone(string? version) =>
        string.Equals(version?.Trim(), None, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The reserved zone tool <c>*</c>: a zone for every registered tool - including ones registered later. Only
/// <see cref="ZoneVersion.None"/> makes sense for it (one version can't fit every tool), so it means "tack is
/// off here". A tool's own zone at the same directory beats it; see <see cref="ConfigCompiler"/>.
/// </summary>
public static class ZoneTool
{
    public const string All = "*";

    public static bool IsAll(string? tool) => string.Equals(tool?.Trim(), All, StringComparison.Ordinal);
}

/// <summary>The pre-zones binding shape: a directory glob mapped to several tools. Read only for migration.</summary>
public sealed class LegacyBinding
{
    public string Glob { get; set; } = "";
    public Dictionary<string, string> Tools { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class TackSettings
{
    /// <summary>What to do when nothing resolves: "passthrough" (default) or "error".</summary>
    public string NoResolution { get; set; } = "passthrough";

    /// <summary>When true, every shim call appends who called it and what it ran to the invocation log
    /// (<c>tack log on</c>; see <see cref="Tack.Core.Diagnostics.ShimLog"/>). Left out of the JSON when false.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Log { get; set; }

    /// <summary>When true, tack is off for this account (<c>tack disable</c>): every shim passes straight through
    /// to the next match on PATH, as if there were no config. The shims stay on the user PATH, so <c>tack enable</c>
    /// is instant. Left out of the JSON when false.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Disabled { get; set; }
}
