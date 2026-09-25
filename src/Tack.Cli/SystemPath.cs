using System.Runtime.Versioning;
using Spectre.Console;
using Tack.Core.Platform;

namespace Tack.Cli;

/// <summary>
/// Runs one edit of the system (machine) PATH - the only PATH tack ever writes. Already admin: the edit runs in
/// this process. Otherwise just that step is relaunched elevated through UAC (the hidden
/// <c>tack apply-machine-path</c>), so the rest of tack never runs as admin. Either way the before/after lands in
/// a timestamped backup under the data dir, which is also how the elevated child hands its result back.
/// Shared by <c>tack setup</c>, the first-run setup, <c>tack doctor --fix</c> and the uninstall hook.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class SystemPath
{
    public enum Outcome { Changed, Unchanged, Declined, Failed }

    public sealed record Result(Outcome Outcome, PathChange? Change = null, string? Backup = null, string? Error = null);

    /// <param name="direct">The edit, when this process is already elevated.</param>
    /// <param name="elevatedArgs">The <c>apply-machine-path</c> options that make the elevated child do the same
    /// edit. Pass every dir explicitly: the child may run as a different (admin) account with its own profile.</param>
    /// <param name="timeout">Bound on the elevated child (an uninstall hook is killed after 30 s); null waits.</param>
    public static Result Edit(Func<PathChange?> direct, string elevatedArgs, TimeSpan? timeout = null)
    {
        string backup = PathFixBackup.NewPath();
        PathChange? change;

        if (Elevation.IsAdministrator())
        {
            try { change = direct(); }
            catch (Exception ex) { return new Result(Outcome.Failed, Error: ex.Message); }
        }
        else
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(backup)!); } catch { /* Save() reports if it can't write */ }
            switch (Elevation.RelaunchElevated($"apply-machine-path \"{backup}\" {elevatedArgs}", timeout))
            {
                case Elevation.RelaunchOutcome.Cancelled: return new Result(Outcome.Declined);
                case Elevation.RelaunchOutcome.Failed: return new Result(Outcome.Failed);
            }
            change = PathFixBackup.ReadMachine(backup); // the elevated child recorded it (null if already in place)
        }

        return change is null
            ? new Result(Outcome.Unchanged)
            : new Result(Outcome.Changed, change, PathFixBackup.Save(backup, change));
    }

    /// <summary>Print where the backup went and the full before/after, so an edit can be reverted by hand.</summary>
    public static void Report(Result result)
    {
        if (result.Change is not { } change) return;
        if (result.Backup is not null)
            AnsiConsole.MarkupLine($"[grey]backup (for manual revert):[/] {Markup.Escape(result.Backup)}");

        // Plain Console.WriteLine for the PATH values - they hold %, ; and [ that Spectre markup would mangle,
        // and the whole point is to show them verbatim so they can be pasted back by hand if an edit goes wrong.
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]full system PATH values, in case you need to revert by hand (also saved in the backup):[/]");
        AnsiConsole.MarkupLine("[grey]before:[/]");
        Console.WriteLine(change.Before);
        AnsiConsole.MarkupLine("[grey]after:[/]");
        Console.WriteLine(change.After);
    }

    public static string Quote(string dir) => $"\"{dir.TrimEnd('\\', '/')}\"";
}
