using Spectre.Console;
using Tack.Core.Platform;

namespace Tack.Cli;

/// <summary>
/// Runs one edit of the user PATH - the only PATH tack ever writes - and keeps the before/after in a timestamped
/// backup. Shared by <c>tack setup</c>, <c>tack doctor --fix</c> and the install and uninstall hooks.
/// </summary>
internal static class UserPath
{
    /// <summary>What an edit did: the change (null if nothing needed writing), where its backup went, or why it
    /// failed.</summary>
    public sealed record Result(PathChange? Change = null, string? Backup = null, string? Error = null);

    public static Result Edit(Func<PathChange?> edit)
    {
        PathChange? change;
        try { change = edit(); }
        catch (Exception ex) { return new Result(Error: ex.Message); }
        return change is null ? new Result() : new Result(change, PathFixBackup.Save(change));
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
        AnsiConsole.MarkupLine("[grey]full user PATH values, in case you need to revert by hand (also saved in the backup):[/]");
        AnsiConsole.MarkupLine("[grey]before:[/]");
        Console.WriteLine(change.Before);
        AnsiConsole.MarkupLine("[grey]after:[/]");
        Console.WriteLine(change.After);
    }
}
