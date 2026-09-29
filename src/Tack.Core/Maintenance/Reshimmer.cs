using System.Text.Json;
using Tack.Core.Config;

namespace Tack.Core.Maintenance;

public sealed class CompileResult
{
    public int ToolsCompiled { get; init; }
    /// <summary>The shims this config needs (valid exposed names only; see <see cref="ShimName"/>).</summary>
    public IReadOnlyList<string> ShimNames { get; init; } = Array.Empty<string>();
    /// <summary>Exposed names that can't be shims, so nothing intercepts them.</summary>
    public IReadOnlyList<string> InvalidNames { get; init; } = Array.Empty<string>();
    /// <summary>Pre-zones binding globs that couldn't be migrated to zones, so resolution ignores them.</summary>
    public IReadOnlyList<string> UnmigratedBindings { get; init; } = Array.Empty<string>();
}

/// <summary>
/// The user's half of a reshim: compiles config.json -> resolved.json, which is all a version, zone or setting
/// change needs. It never touches the shims dir; that's the machine's, written by <see cref="ShimStamper"/> from an
/// elevated process, and only when the set of names (or the shim build) changes.
/// </summary>
public static class Reshimmer
{
    public static CompileResult Compile(CentralConfig config, string resolvedJsonPath)
    {
        var resolved = ConfigCompiler.Compile(config);
        string? resolvedDir = Path.GetDirectoryName(resolvedJsonPath);
        if (!string.IsNullOrEmpty(resolvedDir)) Directory.CreateDirectory(resolvedDir);
        WriteAtomically(resolvedJsonPath, JsonSerializer.Serialize(resolved, TackJson.Default.ResolvedConfig));

        return new CompileResult
        {
            ToolsCompiled = config.Tools.Count,
            ShimNames = ShimName.Exposed(config).ToList(),
            InvalidNames = ShimName.Invalid(config),
            UnmigratedBindings = ZoneRegistry.Unmigrated(config),
        };
    }

    // Write to a temp file and swap it in, so a shim never reads a half-written resolved.json. File.Replace
    // (Win32 ReplaceFile) swaps even while a shim has the old file open - the shim shares delete for exactly this -
    // where a plain rename-over is refused. Retried briefly for an older shim that doesn't share delete yet.
    private static void WriteAtomically(string path, string contents)
    {
        string temp = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temp, contents);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.Replace(temp, path, destinationBackupFileName: null);
                else File.Move(temp, path);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 10)
            {
                Thread.Sleep(50);
            }
            catch
            {
                ShimStamper.TryDelete(temp);
                throw;
            }
        }
    }
}
