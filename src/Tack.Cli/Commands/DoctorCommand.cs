using Spectre.Console;
using Spectre.Console.Cli;
using Tack.Core.Maintenance;

namespace Tack.Cli.Commands;

public sealed class DoctorCommand : Command
{
    public override int Execute(CommandContext context)
    {
        var env = new TackEnvironment();
        var report = PathDoctor.Run(env.Load(), env.ShimsDir,
            t => Environment.GetEnvironmentVariable("PATH", t));

        foreach (var check in report.Checks)
        {
            string glyph = check.Status switch
            {
                CheckStatus.Ok => "[green]  OK [/]",
                CheckStatus.Warn => "[yellow] WARN[/]",
                _ => "[red] FAIL[/]",
            };
            AnsiConsole.MarkupLine($"{glyph}  {Markup.Escape(check.Title)}  [grey]{Markup.Escape(check.Detail)}[/]");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(report.HasProblems
            ? "[yellow]tack doctor found issues above.[/]"
            : "[green]all checks passed.[/]");
        return report.HasProblems ? 1 : 0;
    }
}
