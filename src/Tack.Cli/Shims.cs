using Spectre.Console;
using Tack.Core;
using Tack.Core.Config;
using Tack.Core.Maintenance;

namespace Tack.Cli;

/// <summary>
/// Keeps the machine's shims in step with this user's config. Every config change compiles resolved.json (the
/// user's own file, no admin). Only when that leaves a command name without a shim - a new tool or exposed name -
/// does one UAC prompt run <c>tack elevated shims &lt;names&gt;</c> to stamp them into the admin-owned shims dir.
/// Versions, zones and settings never prompt. Stale shims are left alone (they pass straight through) until
/// <c>tack doctor --fix</c> offers to prune them, since another account may still use them.
/// </summary>
internal static class Shims
{
    /// <summary>The command that retries a skipped shim or PATH step: <c>tack setup</c>, which a dev build
    /// refuses in favour of <c>doctor --fix</c>.</summary>
    public static string RepairCommand => TackProfile.IsDev ? "tack doctor --fix" : "tack setup";

    /// <summary>
    /// Compile <paramref name="config"/>, then stamp any shims it's missing. With <paramref name="checkPayload"/>
    /// (the repair commands), also refresh copies of an older shim build. Returns true when every shim is current.
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

        var plan = ShimStamper.Pending(compiled.ShimNames, env.ShimsDir, payload, checkPayload);
        if (plan.IsEmpty)
        {
            AnsiConsole.MarkupLine("[grey]config compiled; shims up to date[/]"
                + (compiled.ShimNames.Count > 0 ? $" [grey]({Markup.Escape(string.Join(", ", compiled.ShimNames))})[/]" : ""));
            return true;
        }

        string what = plan.Names.Count > 0 ? string.Join(", ", plan.Names) : "the shim support files";
        if (!OperatingSystem.IsWindows() || Elevation.IsAdministrator())
        {
            ShimStamper.Stamp(plan.Names, env.ShimsDir, payload);
        }
        else
        {
            AnsiConsole.MarkupLine($"[grey]writing shims for {Markup.Escape(what)} needs admin; prompting via UAC...[/]");
            if (Elevation.RelaunchElevated("elevated shims " + string.Join(' ', plan.Names)) == Elevation.RelaunchOutcome.Cancelled)
            {
                AnsiConsole.MarkupLine(plan.Names.Count > 0
                    ? $"[yellow]configured, but {Markup.Escape(what)} isn't intercepted until you run [green]{RepairCommand}[/].[/]"
                    : $"[yellow]configured, but the shims may not start until you run [green]{RepairCommand}[/].[/]");
                return false;
            }
        }

        // Judge by what's on disk now, not by the child's exit code: the elevated window is gone, and a copy it
        // couldn't write (in use, AV scan) is simply still pending.
        var left = ShimStamper.Pending(compiled.ShimNames, env.ShimsDir, payload, checkPayload);
        if (left.IsEmpty)
        {
            AnsiConsole.MarkupLine($"[green]shims written:[/] {Markup.Escape(what)}");
            return true;
        }
        string stuck = left.Names.Count > 0 ? string.Join(", ", left.Names) : "the shim support files";
        AnsiConsole.MarkupLine($"[yellow]couldn't write shims for {Markup.Escape(stuck)}[/] [grey](in use? run [green]{RepairCommand}[/] once they're free)[/]");
        return false;
    }

    /// <summary>Offer to remove shims this user's config doesn't use (<c>doctor --fix</c>). Asks first, because
    /// the shims dir is shared and another account may rely on them; skipped in a non-interactive terminal.</summary>
    public static void PruneStale(TackEnvironment env, CentralConfig config)
    {
        var stale = ShimStamper.Stale(ShimName.Exposed(config), env.ShimsDir);
        if (stale.Count == 0) return;

        AnsiConsole.MarkupLine($"[yellow]shims not in your config:[/] {Markup.Escape(string.Join(", ", stale))}");
        AnsiConsole.MarkupLine("[grey]they only pass calls through, but another account on this machine may still use them.[/]");
        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            AnsiConsole.MarkupLine("[grey]run [green]tack doctor --fix[/] in an interactive terminal to remove them.[/]");
            return;
        }
        if (!AnsiConsole.Confirm("Remove them?", defaultValue: true)) return;

        if (!OperatingSystem.IsWindows() || Elevation.IsAdministrator())
        {
            ShimStamper.Prune(stale, env.ShimsDir);
        }
        else
        {
            AnsiConsole.MarkupLine("[grey]removing shims needs admin; prompting via UAC...[/]");
            if (Elevation.RelaunchElevated("elevated prune " + string.Join(' ', stale)) == Elevation.RelaunchOutcome.Cancelled)
            {
                AnsiConsole.MarkupLine("[yellow]elevation declined; shims left in place.[/]");
                return;
            }
        }

        var left = ShimStamper.Stale(ShimName.Exposed(config), env.ShimsDir).Intersect(stale, StringComparer.OrdinalIgnoreCase).ToList();
        var gone = stale.Except(left, StringComparer.OrdinalIgnoreCase).ToList();
        if (gone.Count > 0)
            AnsiConsole.MarkupLine($"[green]removed shims:[/] {Markup.Escape(string.Join(", ", gone))}");
        if (left.Count > 0)
            AnsiConsole.MarkupLine($"[yellow]couldn't remove:[/] {Markup.Escape(string.Join(", ", left))} [grey](in use? try again once they're free)[/]");
    }
}
