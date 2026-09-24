using System.Text.Json;

namespace Tack.Core.Config;

/// <summary>
/// Loads and saves the central config (config.json). A missing file is an empty config, so first-run and
/// the CLI's mutating commands don't need special-casing. Loading migrates pre-zones glob bindings in memory
/// (<see cref="ZoneRegistry.Migrate"/>); the next save persists that. Written indented (via TackJson) since a
/// human occasionally reads it.
/// </summary>
public sealed class ConfigStore
{
    private readonly string _path;

    public ConfigStore(string? path = null) => _path = path ?? TackPaths.ConfigJson;

    public string Path => _path;

    public CentralConfig Load()
    {
        if (!File.Exists(_path)) return new CentralConfig();
        CentralConfig config;
        try
        {
            using var fs = File.OpenRead(_path);
            config = JsonSerializer.Deserialize(fs, TackJson.Default.CentralConfig) ?? new CentralConfig();
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"config.json at {_path} could not be read: {ex.Message}", ex);
        }
        ZoneRegistry.Migrate(config);
        return config;
    }

    public void Save(CentralConfig config)
    {
        string? dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(config, TackJson.Default.CentralConfig));
    }
}
