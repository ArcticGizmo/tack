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
    [Description("Repair what doctor can: regenerate shims, and promote the shims dir to the front of the system PATH (prompts for elevation).")]
    public bool Fix { get; init; }
}

public sealed class DoctorCommand : Command<DoctorSettings>
{
    public override int Execute(CommandContext context, DoctorSettings settings)
    {
        var env = new TackEnvironment();

        if (TackProfile.IsDev)
            AnsiConsole.MarkupLine($"[yellow]profile:[/] dev [grey](isolated data at {Markup.Escape(TackPaths.Root)}; shims here are not wired onto PATH)[/]");
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

        if (TackProfile.IsDev)
        {
            AnsiConsole.MarkupLine("[grey]dev profile: shims are intentionally not on PATH; skipping PATH promotion.[/]");
            return;
        }
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

        PromoteMachinePath();
    }

    [SupportedOSPlatform("windows")]
    private static void PromoteMachinePath()
    {
        var installer = new WindowsPathInstaller();
        string backup = PathFixBackup.NewPath();
        PathChange? machine;

        if (Elevation.IsAdministrator())
        {
            try
            {
                machine = installer.PrependShimsToMachinePath();
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

            switch (Elevation.RelaunchElevated($"apply-machine-path \"{backup}\""))
            {
                case Elevation.RelaunchOutcome.Cancelled:
                    AnsiConsole.MarkupLine("[yellow]elevation declined; system PATH not changed.[/]");
                    return;
                case Elevation.RelaunchOutcome.Failed:
                    AnsiConsole.MarkupLine("[red]elevated PATH update failed.[/] Try running [green]tack doctor --fix[/] from an elevated terminal.");
                    return;
            }
            machine = PathFixBackup.ReadMachine(backup); // the elevated child recorded it (null if it already led)
        }

        if (machine is null)
        {
            AnsiConsole.MarkupLine("[grey]shims dir already leads the system PATH; nothing changed.[/]");
            return;
        }

        string? saved = PathFixBackup.Save(backup, machine);
        AnsiConsole.MarkupLine("[green]shims dir promoted to the front of the system PATH[/] [grey](%VAR% tokens preserved)[/] - open a new terminal to pick it up.");
        if (saved is not null)
            AnsiConsole.MarkupLine($"[grey]backup (for manual revert):[/] {Markup.Escape(saved)}");

        // Plain Console.WriteLine for the PATH values - they hold %, ; and [ that Spectre markup would mangle,
        // and the whole point is to show them verbatim so they can be pasted back if needed.
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]system PATH before:[/]");
        Console.WriteLine(machine.Before);
        AnsiConsole.MarkupLine("[grey]system PATH after:[/]");
        Console.WriteLine(machine.After);
    }
}

public sealed class ApplyMachinePathSettings : CommandSettings
{
    [CommandArgument(0, "[backupPath]")]
    public string? BackupPath { get; init; }
}

/// <summary>
/// Hidden helper: the single elevated step of <c>tack doctor --fix</c> - prepend the shims dir to the machine
/// PATH. Launched via a UAC relaunch so the admin-only write happens in a short-lived elevated process rather
/// than running all of tack as admin. It records the before/after to the given backup file so the parent (whose
/// console outlives this one) can display and keep it. Not meant to be invoked directly.
/// </summary>
public sealed class ApplyMachinePathCommand : Command<ApplyMachinePathSettings>
{
    public override int Execute(CommandContext context, ApplyMachinePathSettings settings)
    {
        if (!OperatingSystem.IsWindows())
            return 1;
        try
        {
            var machine = new WindowsPathInstaller().PrependShimsToMachinePath();
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
