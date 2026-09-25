using System.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Tack.Cli.Commands;

// The invocation log: a setting compiled into resolved.json, so the shim pays nothing for it while it's off.
// Toggling reshims to get the new setting in front of the shims.

// ---- log on --------------------------------------------------------------------------------------

public sealed class LogOnCommand : Command
{
    public override int Execute(CommandContext context)
    {
        var env = new TackEnvironment();
        var config = env.Load();
        if (config.Settings.Log)
        {
            AnsiConsole.MarkupLine($"[yellow]already logging[/] to {Markup.Escape(env.ShimLog)}");
            return 0;
        }

        config.Settings.Log = true;
        env.Save(config);
        AnsiConsole.MarkupLine("[green]shim logging on.[/] Every shim call now records its caller chain, directory and target in:");
        AnsiConsole.MarkupLine($"  {Markup.Escape(env.ShimLog)}");
        AnsiConsole.MarkupLine("[grey]it adds a little to every tool call - [green]tack log off[/] when you're done; [green]tack log open[/] to find it.[/]");
        Mutations.ReportReshim(env.Reshim(config));
        return 0;
    }
}

// ---- log off -------------------------------------------------------------------------------------

public sealed class LogOffCommand : Command
{
    public override int Execute(CommandContext context)
    {
        var env = new TackEnvironment();
        var config = env.Load();
        if (!config.Settings.Log)
        {
            AnsiConsole.MarkupLine("[yellow]logging is already off.[/]");
            return 0;
        }

        config.Settings.Log = false;
        env.Save(config);
        AnsiConsole.MarkupLine("[green]shim logging off.[/] [grey]the log is kept:[/] " + Markup.Escape(env.ShimLog));
        Mutations.ReportReshim(env.Reshim(config));
        return 0;
    }
}

// ---- log open ------------------------------------------------------------------------------------

public sealed class LogOpenCommand : Command
{
    public override int Execute(CommandContext context)
    {
        var env = new TackEnvironment();
        string log = env.ShimLog;
        string dir = Path.GetDirectoryName(log)!;

        // Explorer with the log selected; just the folder when nothing's been logged yet.
        string args;
        if (File.Exists(log))
        {
            AnsiConsole.MarkupLine(Markup.Escape(log));
            args = $"/select,\"{log}\"";
        }
        else
        {
            Directory.CreateDirectory(dir);
            AnsiConsole.MarkupLine($"[yellow]nothing logged yet[/] [grey]in[/] {Markup.Escape(dir)}"
                + (env.Load().Settings.Log ? "" : " [grey](logging is off - [green]tack log on[/])[/]"));
            args = $"\"{dir}\"";
        }

        using var _ = Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = false });
        return 0;
    }
}
