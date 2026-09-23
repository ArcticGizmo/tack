using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tack.Core.Changelog;

/// <summary>
/// Small per-user UI preferences that don't belong in the shim-facing central config: currently just the
/// bookkeeping for the "what's new" changelog popup. Kept in its own file (<c>ui-state.json</c>) so the hot
/// path (config.json -> resolved.json) is never touched by UI concerns, and so the CLI and UI agree on what
/// version the user has already seen. Deliberately tiny.
/// </summary>
public sealed class UiState
{
    /// <summary>The tack version that last ran here. Null on a fresh install - nothing to diff against.</summary>
    public string? LastSeenVersion { get; set; }

    /// <summary>Whether the post-update "what's new" popup fires. The popup's opt-out flips this off.</summary>
    public bool ShowChangelogOnUpdate { get; set; } = true;
}

/// <summary>Source-gen JSON context for <see cref="UiState"/>, kept separate from the config context so the
/// AOT shim's serializer surface stays exactly the two config shapes it needs and nothing more.</summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(UiState))]
public partial class UiStateJson : JsonSerializerContext { }

/// <summary>
/// Loads and saves <see cref="UiState"/> to <c>ui-state.json</c>. A missing or unreadable file is treated as
/// defaults, and a save failure is swallowed - UI preferences are never worth crashing over. Mirrors
/// <c>ConfigStore</c>'s shape.
/// </summary>
public sealed class UiStateStore
{
    private readonly string _path;

    public UiStateStore(string? path = null) => _path = path ?? TackPaths.UiStateJson;

    public string Path => _path;

    public UiState Load()
    {
        try
        {
            if (!File.Exists(_path)) return new UiState();
            using var fs = File.OpenRead(_path);
            return JsonSerializer.Deserialize(fs, UiStateJson.Default.UiState) ?? new UiState();
        }
        catch
        {
            return new UiState();
        }
    }

    public void Save(UiState state)
    {
        try
        {
            string? dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_path, JsonSerializer.Serialize(state, UiStateJson.Default.UiState));
        }
        catch
        {
            // Best-effort: a failure to persist a UI preference must never break the app.
        }
    }
}
