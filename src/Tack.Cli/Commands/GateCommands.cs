using Spectre.Console;
using Spectre.Console.Cli;

namespace Tack.Cli.Commands;

// Switching tack off and on: a setting compiled into resolved.json, like the invocation log. It needs no admin,
// and open shells see it on their next tool call. The shims stay on the user PATH and just pass calls through.

// ---- disable -------------------------------------------------------------------------------------

public sealed class DisableCommand : Command
{
    public override int Execute(CommandContext context)
    {
        var env = new TackEnvironment();
        var config = env.Load();
        if (config.Settings.Disabled)
        {
            AnsiConsole.MarkupLine("[yellow]already disabled.[/] Run [green]tack enable[/] to turn tack back on.");
            return 0;
        }

        config.Settings.Disabled = true;
        env.Save(config);
        AnsiConsole.MarkupLine("[green]tack disabled for you.[/] Your tool calls now fall through to the rest of PATH, in every shell.");
        AnsiConsole.MarkupLine("[grey]tools, zones and use still work; run [green]tack enable[/] to turn it back on.[/]");
        Shims.Sync(env, config);
        return 0;
    }
}

// ---- enable --------------------------------------------------------------------------------------

public sealed class EnableCommand : Command
{
    public override int Execute(CommandContext context)
    {
        var env = new TackEnvironment();
        var config = env.Load();
        if (!config.Settings.Disabled)
        {
            AnsiConsole.MarkupLine("[yellow]already enabled.[/] Nothing to do.");
            return 0;
        }

        config.Settings.Disabled = false;
        env.Save(config);
        AnsiConsole.MarkupLine("[green]tack enabled.[/] Interception is live again.");
        Shims.Sync(env, config);
        return 0;
    }
}
