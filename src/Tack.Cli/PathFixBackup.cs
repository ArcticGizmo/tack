using System.Text.Json;
using System.Text.Json.Serialization;
using Tack.Core;
using Tack.Core.Platform;

namespace Tack.Cli;

/// <summary>
/// Persists the before/after of a <c>tack doctor --fix</c> PATH edit to a timestamped JSON file under the tack
/// data dir - a safety net (and audit trail) while the machine-PATH rewrite earns trust: if an edit goes wrong,
/// the previous value is recorded to paste back by hand. It also carries the machine before/after out of the
/// short-lived elevated child process (which prints to a window that vanishes) back to the parent to display.
/// </summary>
internal static class PathFixBackup
{
    public static string NewPath() =>
        Path.Combine(TackPaths.Root, "path-backups", $"path-fix-{DateTime.Now:yyyyMMdd-HHmmss}.json");

    /// <summary>Best-effort write; returns the path on success, null if it couldn't be saved.</summary>
    public static string? Save(string path, PathChange? machine)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var record = new Record
            {
                Timestamp = DateTime.Now.ToString("o"),
                Machine = Scope.From(machine),
            };
            File.WriteAllText(path, JsonSerializer.Serialize(record, PathFixBackupJson.Default.Record));
            return path;
        }
        catch { return null; }
    }

    /// <summary>Read back the machine edit an elevated child recorded (null if none/unreadable).</summary>
    public static PathChange? ReadMachine(string path)
    {
        try
        {
            var record = JsonSerializer.Deserialize(File.ReadAllText(path), PathFixBackupJson.Default.Record);
            return record?.Machine is { } m ? new PathChange("machine", m.Before, m.After) : null;
        }
        catch { return null; }
    }

    internal sealed class Record
    {
        public string Timestamp { get; set; } = "";
        public Scope? Machine { get; set; }
    }

    internal sealed class Scope
    {
        public string Before { get; set; } = "";
        public string After { get; set; } = "";
        public static Scope? From(PathChange? c) => c is null ? null : new Scope { Before = c.Before, After = c.After };
    }
}

// Source-generated rather than reflection-based: the published CLI is trimmed, which switches reflection-based
// System.Text.Json off - and Save/ReadMachine swallow the resulting exception, so the backup would silently vanish.
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PathFixBackup.Record))]
internal sealed partial class PathFixBackupJson : JsonSerializerContext { }
