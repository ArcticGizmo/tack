using System.ComponentModel;
using System.Runtime.Versioning;
using Spectre.Console;
using Spectre.Console.Cli;
using Tack.Core;
using Tack.Core.Maintenance;
using Tack.Core.Platform;

namespace Tack.Cli.Commands;

public sealed class DoctorSettings : CommandSettings
{
    [CommandOption("--fix")]
    [Description("Repair what doctor can: regenerate shims, and promote the shims dir to the front of the system PATH (prompts for elevation). A dev build goes just behind the release tack's shims.")]
    public bool Fix { get; init; }
}

public sealed class DoctorCommand : Command<DoctorSettings>
{
    public override int Execute(CommandContext context, DoctorSettings settings)
    {
        var env = new TackEnvironment();

        if (TackProfile.IsDev)
            AnsiConsole.MarkupLine($"[yellow]profile:[/] dev [grey](isolated data at {Markup.Escape(TackPaths.Root)}; its shims go at the front of the system PATH, just behind any release tack)[/]");
        else
            AnsiConsole.MarkupLine($"[grey]data:[/] {Markup.Escape(TackPaths.Root)}");

        if (settings.Fix)
        {
            RunFix(env);
            AnsiConsole.WriteLine();
        }

        var report = PathDoctor.Run(env.Load(), env.ShimsDir,
            t => Environment.GetEnvironmentVariable("PATH", t),
            activeShimsDir: env.ActiveShimsDir, disabled: env.IsDisabled);

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

        // Regenerate shims + prune stale ones (into the active dir, so this works even while disabled).
        Mutations.ReportReshim(env.Reshim(env.Load()));

        if (env.IsDisabled)
        {
            AnsiConsole.MarkupLine("[yellow]tack is disabled; skipping PATH promotion. Run [green]tack enable[/] first, then re-run.[/]");
            return;
        }
        if (!OperatingSystem.IsWindows())
        {
            AnsiConsole.MarkupLine("[yellow]PATH promotion is Windows-only; skipped.[/]");
            return;
        }

        // Both profiles go to the front of the MACHINE PATH (a user-PATH entry loses to every system-wide install).
        // A dev instance lands directly behind the release shims, so it beats real installs but never the release tack.
        PromoteMachinePath(env.ShimsDir, TackProfile.IsDev ? TackPaths.ReleaseShimsDirs : null);
    }

    [SupportedOSPlatform("windows")]
    private static void PromoteMachinePath(string shimsDir, IReadOnlyList<string>? behind)
    {
        var installer = new WindowsPathInstaller(shimsDir: shimsDir);
        string backup = PathFixBackup.NewPath();
        PathChange? machine;
        string where = behind is null ? "to the front of the system PATH" : "to the front of the system PATH, behind the release tack's shims";

        if (Elevation.IsAdministrator())
        {
            try
            {
                machine = installer.PromoteShimsOnMachinePath(behind);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]couldn't update the system PATH:[/] {Markup.Escape(ex.Message)}");
                return;
            }
        }
        else
        {
            AnsiConsole.MarkupLine("[grey]editing the system PATH needs elevation; prompting for admin...[/]");
            try { Directory.CreateDirectory(Path.GetDirectoryName(backup)!); } catch { /* Save() reports if it can't write */ }

            // Pass the target explicitly: the elevated child can't be relied on to inherit a TACK_DEV override.
            string args = $"apply-machine-path \"{backup}\" --shims \"{shimsDir}\""
                + string.Concat((behind ?? Array.Empty<string>()).Select(b => $" --behind \"{b}\""));
            switch (Elevation.RelaunchElevated(args))
            {
                case Elevation.RelaunchOutcome.Cancelled:
                    AnsiConsole.MarkupLine("[yellow]elevation declined; system PATH not changed.[/]");
                    return;
                case Elevation.RelaunchOutcome.Failed:
                    AnsiConsole.MarkupLine("[red]elevated PATH update failed.[/] Try running [green]tack doctor --fix[/] from an elevated terminal.");
                    return;
            }
            machine = PathFixBackup.ReadMachine(backup); // the elevated child recorded it (null if already placed)
        }

        // doctor --fix only ever edits the system PATH; the user PATH is never touched.
        if (machine is null)
        {
            AnsiConsole.MarkupLine("[grey]shims dir is already in place on the system PATH; nothing changed.[/]");
            return;
        }

        string? saved = PathFixBackup.Save(backup, machine);
        AnsiConsole.MarkupLine($"[green]shims dir promoted {Markup.Escape(where)}[/] [grey](%VAR% tokens preserved)[/] - open a new terminal to pick it up.");
        if (behind is not null)
            AnsiConsole.MarkupLine("[grey]the release tack still answers first for commands it shims; run its [green]tack disable[/] to hand them over to dev.[/]");
        if (saved is not null)
            AnsiConsole.MarkupLine($"[grey]backup (for manual revert):[/] {Markup.Escape(saved)}");

        // Plain Console.WriteLine for the PATH values - they hold %, ; and [ that Spectre markup would mangle,
        // and the whole point is to show them verbatim so they can be pasted back by hand if an edit goes wrong.
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]full system PATH values, in case you need to revert by hand (also saved in the backup):[/]");
        AnsiConsole.MarkupLine("[grey]before:[/]");
        Console.WriteLine(machine.Before);
        AnsiConsole.MarkupLine("[grey]after:[/]");
        Console.WriteLine(machine.After);
    }
}

public sealed class ApplyMachinePathSettings : CommandSettings
{
    [CommandArgument(0, "[backupPath]")]
    public string? BackupPath { get; init; }

    [CommandOption("--shims <DIR>")]
    public string? ShimsDir { get; init; }

    [CommandOption("--behind <DIR>")]
    public string[] Behind { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Hidden helper: the single elevated step of <c>tack doctor --fix</c> - promote the shims dir on the machine
/// PATH (to the front, or just behind the <c>--behind</c> dirs for a dev instance). Launched via a UAC relaunch so
/// the admin-only write happens in a short-lived elevated process rather than running all of tack as admin. It
/// records the before/after to the given backup file so the parent (whose console outlives this one) can display
/// and keep it. Not meant to be invoked directly.
/// </summary>
public sealed class ApplyMachinePathCommand : Command<ApplyMachinePathSettings>
{
    public override int Execute(CommandContext context, ApplyMachinePathSettings settings)
    {
        if (!OperatingSystem.IsWindows())
            return 1;
        try
        {
            var installer = new WindowsPathInstaller(shimsDir: settings.ShimsDir is { Length: > 0 } s ? s : null);
            var machine = installer.PromoteShimsOnMachinePath(settings.Behind.Length > 0 ? settings.Behind : null);
            if (!string.IsNullOrEmpty(settings.BackupPath))
                PathFixBackup.Save(settings.BackupPath, machine);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}
