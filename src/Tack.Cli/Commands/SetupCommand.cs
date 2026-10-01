using System.ComponentModel;
using System.Runtime.Versioning;
using Spectre.Console;
using Spectre.Console.Cli;
using Tack.Core;

namespace Tack.Cli.Commands;

public sealed class SetupSettings : CommandSettings
{
    [CommandOption("--remove")]
    [Description("Take this profile's entries off your user PATH instead (uninstall does this for a release install).")]
    public bool Remove { get; init; }
}

/// <summary>
/// <c>tack setup</c>: put tack on your USER PATH - shims dir at the front, tack.exe's dir at the end - and stamp the
/// shims your config needs. The install hook does the same on every install and update; this is the repair, and
/// what install.ps1 runs to show you the result. A dev build's shims go just behind the release tack's instead.
/// Nothing here needs admin, and tack never touches the system PATH.
/// </summary>
public sealed class SetupCommand : Command<SetupSettings>
{
    public override int Execute(CommandContext context, SetupSettings settings)
    {
        if (!OperatingSystem.IsWindows()) return 0;
        return settings.Remove ? PathSetup.Remove() : PathSetup.Run();
    }
}

[SupportedOSPlatform("windows")]
internal static class PathSetup
{
    public static int Run()
    {
        var env = new TackEnvironment();
        var installer = env.PathInstaller();
        string where = TackProfile.IsDev ? "your user PATH, behind the release tack's shims" : "your user PATH";

        var result = UserPath.Edit(installer.Register);
        int rc;
        if (result.Error is { } err)
        {
            AnsiConsole.MarkupLine($"[red]couldn't update your user PATH:[/] {Markup.Escape(err)}");
            rc = 1;
        }
        else if (result.Change is null)
        {
            AnsiConsole.MarkupLine($"[green]tack is on {where}.[/] [grey]nothing to change.[/]");
            rc = 0;
        }
        else
        {
            AnsiConsole.MarkupLine($"[green]tack added to {where}[/] [grey](%VAR% tokens preserved)[/] - open a new terminal to pick it up.");
            UserPath.Report(result);
            rc = 0;
        }

        bool shims = Shims.Sync(env, env.Load(), checkPayload: true);
        return rc != 0 ? rc : shims ? 0 : 1;
    }

    public static int Remove()
    {
        var result = UserPath.Edit(new TackEnvironment().PathInstaller().Unregister);
        if (result.Error is { } err)
        {
            AnsiConsole.MarkupLine($"[red]couldn't update your user PATH:[/] {Markup.Escape(err)}");
            return 1;
        }
        if (result.Change is null)
        {
            AnsiConsole.MarkupLine("[grey]tack isn't on your user PATH; nothing to change.[/]");
            return 0;
        }
        AnsiConsole.MarkupLine("[green]tack removed from your user PATH[/] - open a new terminal to pick it up. Your config and shims are left in place.");
        UserPath.Report(result);
        return 0;
    }
}
