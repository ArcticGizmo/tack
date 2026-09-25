using System.Text;

namespace Tack.Core.Diagnostics;

/// <summary>One process in a shim's caller chain: its pid and full image path (null when it couldn't be read,
/// e.g. an elevated or protected process).</summary>
public sealed record CallerProcess(int Pid, string? ImagePath);

/// <summary>Everything the shim knows about one invocation, captured after it has decided what to run.</summary>
public sealed class ShimLogEntry
{
    public DateTime Time { get; init; } = DateTime.Now;
    public int Pid { get; init; }
    public required string Exposed { get; init; }
    public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();
    public string Cwd { get; init; } = "";
    public string Source { get; init; } = "";
    public string? Version { get; init; }
    public string? Detail { get; init; }

    /// <summary>The executable the shim is about to run; null when it's about to fail instead.</summary>
    public string? Target { get; init; }
    public string? Error { get; init; }

    /// <summary>Parent first, then its parent, and so on.</summary>
    public IReadOnlyList<CallerProcess> Callers { get; init; } = Array.Empty<CallerProcess>();

    /// <summary>Set when the chain was cut short: the next ancestor has exited (its pid may since have been
    /// reused) or couldn't be opened.</summary>
    public string? CallersEnd { get; init; }
}

/// <summary>
/// The opt-in invocation log (<c>tack log on</c>): one human-readable block per shim call, recording who called it,
/// from where, and what it ran. For tracking down a process that's invoking a tool from somewhere unexpected.
///
/// <para>The shim is the only writer, and many shims can run at once (npm spawning node, a build fanning out), so
/// each entry is one exclusive append, retried briefly and then dropped: logging must never break or noticeably
/// stall the tool it wraps. The file rolls over to <c>shim.log.1</c> past <see cref="MaxBytes"/>, so a log left on
/// can't grow without bound.</para>
/// </summary>
public static class ShimLog
{
    public const long MaxBytes = 5 * 1024 * 1024;

    /// <summary>The log sits beside the resolved.json the shim read, so it follows the profile (tack vs
    /// tack (Dev)) the same way the shim's config does.</summary>
    public static string PathFor(string resolvedJsonPath) =>
        Path.Combine(Path.GetDirectoryName(resolvedJsonPath) ?? "", "logs", "shim.log");

    public static string Format(ShimLogEntry e)
    {
        var sb = new StringBuilder();
        sb.Append($"{e.Time:yyyy-MM-dd HH:mm:ss.fff}  {e.Exposed}");
        if (e.Version is not null) sb.Append($" {e.Version}");
        sb.Append($"  ({e.Source}{(string.IsNullOrEmpty(e.Detail) ? "" : ": " + e.Detail)})  pid {e.Pid}\n");

        Line(sb, "args", e.Args.Count == 0 ? "(none)" : string.Join(' ', e.Args.Select(Quote)));
        Line(sb, "cwd", e.Cwd);
        if (e.Target is not null) Line(sb, "runs", e.Target);
        if (e.Error is not null) Line(sb, "error", e.Error);

        if (e.Callers.Count == 0)
            Line(sb, "caller", e.CallersEnd ?? "(unknown)");
        for (int i = 0; i < e.Callers.Count; i++)
        {
            var c = e.Callers[i];
            Line(sb, i == 0 ? "caller" : "", $"[{c.Pid}] {c.ImagePath ?? "(image path unavailable)"}");
        }
        if (e.Callers.Count > 0 && e.CallersEnd is not null)
            Line(sb, "", e.CallersEnd);

        sb.Append('\n');
        return sb.ToString();
    }

    /// <summary>Append one formatted entry. Never throws; returns false if the entry was dropped.</summary>
    public static bool Append(string path, string text)
    {
        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
            RollOverIfFull(path);

            byte[] bytes = Encoding.UTF8.GetBytes(text);
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    // Exclusive for writing (readers are fine) so concurrent shims can't interleave or overwrite
                    // each other's entries; share delete so a roll-over never waits on a writer.
                    using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete);
                    fs.Write(bytes);
                    return true;
                }
                catch (IOException) when (attempt < 20)
                {
                    Thread.Sleep(5);
                }
            }
        }
        catch
        {
            return false;
        }
    }

    // Two shims can race here; the worst case is losing some old entries, which is fine for a debug log.
    private static void RollOverIfFull(string path)
    {
        try
        {
            if (new FileInfo(path) is { Exists: true, Length: >= MaxBytes })
                File.Move(path, path + ".1", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void Line(StringBuilder sb, string label, string value) =>
        sb.Append($"  {label,-7} {value}\n");

    private static string Quote(string arg) =>
        arg.Length == 0 || arg.Any(c => char.IsWhiteSpace(c) || c == '"') ? $"\"{arg.Replace("\"", "\\\"")}\"" : arg;
}
