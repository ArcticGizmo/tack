using Spectre.Console;
using Tack.Core.Config;
using Tack.Core.Maintenance;

namespace Tack.Cli;

/// <summary>
/// Keeps this account's shims in step with its config. Every config change compiles resolved.json, then stamps a
/// shim for any command name that lacks one and removes the shims for names the config no longer lists. The shims
/// dir is this account's own, so none of it needs admin.
/// </summary>
internal static class Shims
{
    /// <summary>The command that retries a skipped shim or PATH step.</summary>
    public const string RepairCommand = "tack setup";

    /// <summary>
    /// Compile <paramref name="config"/>, stamp any shims it's missing and prune the ones it no longer needs. With
    /// <paramref name="checkPayload"/> (the repair commands), also refresh copies of an older shim build. Returns
    /// true when every shim is current.
    /// </summary>
    public static bool Sync(TackEnvironment env, CentralConfig config, bool checkPayload = false)
    {
        var compiled = env.Compile(config);
        foreach (var bad in compiled.InvalidNames)
            AnsiConsole.MarkupLine($"[yellow]not a valid command name, so not intercepted:[/] {Markup.Escape(bad)} [grey](fix it in config.json)[/]");
        foreach (var glob in compiled.UnmigratedBindings)
            AnsiConsole.MarkupLine($"[yellow]old binding '{Markup.Escape(glob)}' no longer applies[/] [grey](zones take a plain directory; see tack doctor)[/]");

        var payload = env.ShimPayload();
        if (payload.IsMissing)
        {
            AnsiConsole.MarkupLine("[yellow]resolved.json updated, but tack-shim.exe was not found next to tack, so no shims can be stamped (expected under a dev `dotnet run`).[/]");
            return false;
        }

        var pruned = ShimStamper.Prune(ShimStamper.Stale(compiled.ShimNames, env.ShimsDir), env.ShimsDir);
        if (pruned.Pruned.Count > 0)
            AnsiConsole.MarkupLine($"[grey]removed shims no longer in your config:[/] {Markup.Escape(string.Join(", ", pruned.Pruned))}");
        if (pruned.Locked.Count > 0)
            AnsiConsole.MarkupLine($"[yellow]couldn't remove:[/] {Markup.Escape(string.Join(", ", pruned.Locked))} [grey](in use? they go on the next change)[/]");

        var plan = ShimStamper.Pending(compiled.ShimNames, env.ShimsDir, payload, checkPayload);
        if (plan.IsEmpty)
        {
            AnsiConsole.MarkupLine("[grey]config compiled; shims up to date[/]"
                + (compiled.ShimNames.Count > 0 ? $" [grey]({Markup.Escape(string.Join(", ", compiled.ShimNames))})[/]" : ""));
            return true;
        }

        string what = plan.Names.Count > 0 ? string.Join(", ", plan.Names) : "the shim support files";
        var stamped = ShimStamper.Stamp(plan.Names, env.ShimsDir, payload);
        if (stamped.Locked.Count == 0)
        {
            AnsiConsole.MarkupLine($"[green]shims written:[/] {Markup.Escape(what)}");
            return true;
        }
        AnsiConsole.MarkupLine($"[yellow]couldn't write shims:[/] {Markup.Escape(string.Join(", ", stamped.Locked.Select(Path.GetFileName)))} [grey](in use? run [green]{RepairCommand}[/] once they're free)[/]");
        return false;
    }
}
