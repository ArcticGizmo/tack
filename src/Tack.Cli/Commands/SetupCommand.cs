using System.Runtime.Versioning;
using Spectre.Console;
using Spectre.Console.Cli;
using Tack.Core;
using Tack.Core.Platform;

namespace Tack.Cli.Commands;

/// <summary>
/// <c>tack setup</c>: put tack on the SYSTEM PATH - shims dir at the front, tack.exe's dir at the end. This is
/// the install's PATH step. The Velopack install hook can't do it unelevated (it's killed after 30 s, too short
/// to sit on a UAC prompt) and must never fall back to the user PATH, so it runs here instead: from the first
/// run after Setup, from install.ps1 in the user's own terminal, or by hand to retry after declining UAC.
/// </summary>
public sealed class SetupCommand : Command
{
    public override int Execute(CommandContext context) =>
        OperatingSystem.IsWindows() ? PathSetup.Run() : 0;
}

[SupportedOSPlatform("windows")]
internal static class PathSetup
{
    // Setup's first-run launch and install.ps1 can both reach here at once; only one of them should prompt.
    // The loser waits, then finds the PATH already wired and says so.
    private const string GateName = @"Local\tack-path-setup";

    public static int Run()
    {
        if (TackProfile.IsDev)
        {
            AnsiConsole.MarkupLine("[yellow]dev build:[/] its shims go just behind the release tack's; use [green]tack doctor --fix[/] instead.");
            return 1;
        }

        using var gate = new Mutex(false, GateName);
        bool owned;
        try { owned = gate.WaitOne(TimeSpan.FromMinutes(10)); }
        catch (AbandonedMutexException) { owned = true; }
        try
        {
            return Apply(new WindowsPathInstaller(shimsDir: new TackEnvironment().ShimsDir));
        }
        finally
        {
            if (owned) gate.ReleaseMutex();
        }
    }

    private static int Apply(WindowsPathInstaller installer)
    {
        if (!installer.NeedsRegister())
        {
            AnsiConsole.MarkupLine("[green]tack is on the system PATH.[/] [grey]nothing to change.[/]");
            return 0;
        }

        if (!Elevation.IsAdministrator())
            AnsiConsole.MarkupLine("[grey]tack goes on the system PATH (never your user PATH), which needs admin; prompting via UAC...[/]");

        var result = SystemPath.Edit(() => installer.Register(),
            $"--shims {SystemPath.Quote(installer.ShimsDir)} --install-dir {SystemPath.Quote(installer.InstallDir)}");

        string retry = Markup.Escape(Path.Combine(installer.InstallDir, "tack.exe")) + " setup";
        switch (result.Outcome)
        {
            case SystemPath.Outcome.Declined:
                AnsiConsole.MarkupLine("[yellow]elevation declined; system PATH not changed.[/] tack won't intercept tool calls until it's on PATH.");
                AnsiConsole.MarkupLine($"[grey]run[/] [green]{retry}[/] [grey]to try again.[/]");
                return 1;
            case SystemPath.Outcome.Failed:
                AnsiConsole.MarkupLine(result.Error is { } err
                    ? $"[red]couldn't update the system PATH:[/] {Markup.Escape(err)}"
                    : "[red]elevated PATH update failed.[/]");
                AnsiConsole.MarkupLine($"[grey]run[/] [green]{retry}[/] [grey]from an elevated terminal to try again.[/]");
                return 1;
            case SystemPath.Outcome.Unchanged:
                AnsiConsole.MarkupLine("[green]tack is on the system PATH.[/] [grey]nothing to change.[/]");
                return 0;
        }

        AnsiConsole.MarkupLine("[green]tack added to the system PATH[/] [grey](shims dir first, tack.exe's dir last; %VAR% tokens preserved)[/] - open a new terminal to pick it up.");
        SystemPath.Report(result);
        return 0;
    }
}
