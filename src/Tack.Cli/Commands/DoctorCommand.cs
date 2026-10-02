using System.ComponentModel;
using System.Runtime.Versioning;
using Spectre.Console;
using Spectre.Console.Cli;
using Tack.Core;
using Tack.Core.Installs;
using Tack.Core.Maintenance;
using Tack.Core.Resolution;

namespace Tack.Cli.Commands;

public sealed class DoctorSettings : CommandSettings
{
    [CommandOption("--fix")]
    [Description("Repair what doctor can: restamp shims, remove ones your config doesn't use, delete install folders tack made that nothing is registered for, and move the shims dir back to the front of your user PATH. A dev build goes just behind the release tack's shims.")]
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

        var config = env.Load();
        var installs = new Installer(TackPaths.User.InstallsDir);
        var ownFolders = new List<string> { env.ShimsDir, TackPaths.User.Root, env.InstallDir };
        if (Directory.Exists(installs.Root)) ownFolders.Add(installs.Root); // what's in it runs in your sessions too
        var report = PathDoctor.Run(config, env.ShimsDir, CommandSearch.Current(), ownFolders: ownFolders,
            unregisteredInstalls: installs.Unregistered(config), installLeftovers: installs.Leftovers());

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
        FixInstalls(env);

        if (!OperatingSystem.IsWindows())
        {
            AnsiConsole.MarkupLine("[yellow]PATH repair is Windows-only; skipped.[/]");
            return;
        }
        FixUserPath(env);
    }

    // Delete install folders tack made that nothing is registered for, and what unfinished installs left. Folders
    // tack didn't make are only ever reported.
    private static void FixInstalls(TackEnvironment env)
    {
        var installs = new Installer(TackPaths.User.InstallsDir);
        try
        {
            foreach (var folder in installs.Unregistered(env.Load()).Where(f => f.Owned))
            {
                var result = installs.Remove(folder.Path);
                AnsiConsole.MarkupLine(result.Leftover is null
                    ? $"[green]deleted[/] {Markup.Escape(folder.Path)} [grey](tack installed it; nothing is registered for it)[/]"
                    : $"[yellow]moved aside but couldn't delete yet:[/] {Markup.Escape(result.Leftover)}");
            }
            if (installs.Leftovers().Count > 0)
            {
                var left = installs.SweepLeftovers();
                AnsiConsole.MarkupLine(left.Count == 0
                    ? "[green]cleaned up after an unfinished install or removal[/]"
                    : $"[yellow]still in use, so not deleted yet:[/] {Markup.Escape(string.Join(", ", left))}");
            }
        }
        catch (InstallException e)
        {
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(e.Message)}[/]");
        }
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
