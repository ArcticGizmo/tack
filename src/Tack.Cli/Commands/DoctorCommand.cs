using System.ComponentModel;
using System.Runtime.Versioning;
using Spectre.Console;
using Spectre.Console.Cli;
using Tack.Core;
using Tack.Core.Maintenance;
using Tack.Core.Resolution;

namespace Tack.Cli.Commands;

public sealed class DoctorSettings : CommandSettings
{
    [CommandOption("--fix")]
    [Description("Repair what doctor can: restamp shims, remove ones your config doesn't use, and move the shims dir back to the front of your user PATH. A dev build goes just behind the release tack's shims.")]
    public bool Fix { get; init; }
}

public sealed class DoctorCommand : Command<DoctorSettings>
{
    public override int Execute(CommandContext context, DoctorSettings settings)
    {
        var env = new TackEnvironment();

        if (TackProfile.IsDev)
            AnsiConsole.MarkupLine($"[yellow]profile:[/] dev [grey](isolated data at {Markup.Escape(TackPaths.User.Root)}; its shims go on your user PATH, just behind any release tack)[/]");
        else
            AnsiConsole.MarkupLine($"[grey]data:[/] {Markup.Escape(TackPaths.User.Root)}");
        AnsiConsole.MarkupLine($"[grey]install:[/] {Markup.Escape(env.InstallDir)}");

        if (settings.Fix)
        {
            RunFix(env);
            AnsiConsole.WriteLine();
        }

        var report = PathDoctor.Run(env.Load(), env.ShimsDir, CommandSearch.Current());

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

    private static void RunFix(TackEnvironment env)
    {
        AnsiConsole.MarkupLine("[grey]fixing...[/]");

        // Recompile, restamp anything missing or from an older shim build, and drop shims the config doesn't use.
        Shims.Sync(env, env.Load(), checkPayload: true);

        if (!OperatingSystem.IsWindows())
        {
            AnsiConsole.MarkupLine("[yellow]PATH repair is Windows-only; skipped.[/]");
            return;
        }
        FixUserPath(env);
    }

    [SupportedOSPlatform("windows")]
    private static void FixUserPath(TackEnvironment env)
    {
        var result = UserPath.Edit(env.PathInstaller().Register);
        if (result.Error is { } err)
        {
            AnsiConsole.MarkupLine($"[red]couldn't update your user PATH:[/] {Markup.Escape(err)}");
            return;
        }
        if (result.Change is null)
        {
            AnsiConsole.MarkupLine("[grey]shims dir is already in place on your user PATH; nothing changed.[/]");
            return;
        }

        string where = TackProfile.IsDev ? "on your user PATH, behind the release tack's shims" : "to the front of your user PATH";
        AnsiConsole.MarkupLine($"[green]shims dir moved {Markup.Escape(where)}[/] [grey](%VAR% tokens preserved)[/] - open a new terminal to pick it up.");
        if (TackProfile.IsDev)
            AnsiConsole.MarkupLine("[grey]the release tack still answers first for commands it shims; run its [green]tack disable[/] to hand them over to dev.[/]");
        UserPath.Report(result);
    }
}
