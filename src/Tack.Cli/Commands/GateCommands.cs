using Spectre.Console;
using Spectre.Console.Cli;
using Tack.Core.Maintenance;

namespace Tack.Cli.Commands;

// ---- disable -------------------------------------------------------------------------------------

public sealed class DisableCommand : Command
{
    public override int Execute(CommandContext context)
    {
        var env = new TackEnvironment();
        var result = ShimGate.Disable(env.ShimsDir, env.DisabledShimsDir);

        switch (result.Action)
        {
            case GateAction.Disabled:
                AnsiConsole.MarkupLine("[green]tack disabled.[/] Tools now fall through to the real PATH.");
                AnsiConsole.MarkupLine($"[grey]shims parked at:[/] {Markup.Escape(result.Detail)}");
                AnsiConsole.MarkupLine("[grey]tools add/remove, bind and use still work (they configure the parked dir); run [green]tack enable[/] to go live.[/]");
                break;
            case GateAction.AlreadyDisabled:
                AnsiConsole.MarkupLine("[yellow]already disabled.[/] Run [green]tack enable[/] to turn tack back on.");
                break;
        }
        return 0;
    }
}

// ---- enable --------------------------------------------------------------------------------------

public sealed class EnableCommand : Command
{
    public override int Execute(CommandContext context)
    {
        var env = new TackEnvironment();
        var result = ShimGate.Enable(env.ShimsDir, env.DisabledShimsDir);

        switch (result.Action)
        {
            case GateAction.Enabled:
                AnsiConsole.MarkupLine("[green]tack enabled.[/] Interception is live again.");
                AnsiConsole.MarkupLine($"[grey]shims dir:[/] {Markup.Escape(result.Detail)}");
                break;
            case GateAction.AlreadyEnabled:
                AnsiConsole.MarkupLine("[yellow]already enabled.[/] Nothing to do.");
                break;
            case GateAction.Conflict:
                AnsiConsole.MarkupLine($"[red]can't enable:[/] a non-empty shims dir already exists at {Markup.Escape(result.Detail)}.");
                AnsiConsole.MarkupLine($"[grey]resolve by removing it (or the parked [green]{Markup.Escape(env.DisabledShimsDir)}[/]) and re-running.[/]");
                return 1;
        }
        return 0;
    }
}
