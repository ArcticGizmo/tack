using System.Text.Json;
using System.Text.Json.Serialization;
using Tack.Core;
using Tack.Core.Platform;

namespace Tack.Cli;

/// <summary>
/// Persists the before/after of a user PATH edit to a timestamped JSON file in the data folder's
/// <c>path-backups</c> - a safety net (and audit trail): if an edit goes wrong, the previous value is recorded to
/// paste back by hand.
/// </summary>
internal static class PathFixBackup
{
    /// <summary>Best-effort write; returns the path on success, null if it couldn't be saved.</summary>
    public static string? Save(PathChange change)
    {
        try
        {
            string path = Path.Combine(TackPaths.User.PathBackupsDir, $"path-fix-{DateTime.Now:yyyyMMdd-HHmmss-fff}.json");
            Directory.CreateDirectory(TackPaths.User.PathBackupsDir);
            var record = new Record
            {
                Timestamp = DateTime.Now.ToString("o"),
                User = new Scope { Before = change.Before, After = change.After },
            };
            File.WriteAllText(path, JsonSerializer.Serialize(record, PathFixBackupJson.Default.Record));
            return path;
        }
        catch { return null; }
    }

    internal sealed class Record
    {
        public string Timestamp { get; set; } = "";
        public Scope? User { get; set; }
    }

    internal sealed class Scope
    {
        public string Before { get; set; } = "";
        public string After { get; set; } = "";
    }
}

// Source-generated rather than reflection-based: the published CLI is trimmed, which switches reflection-based
// System.Text.Json off - and Save swallows the resulting exception, so the backup would silently vanish.
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PathFixBackup.Record))]
internal sealed partial class PathFixBackupJson : JsonSerializerContext { }
