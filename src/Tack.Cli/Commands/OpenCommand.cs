using System.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Tack.Cli.Commands;

public sealed class OpenCommand : Command
{
    public override int Execute(CommandContext context)
    {
        string exe = OperatingSystem.IsWindows() ? "tack-ui.exe" : "tack-ui";

        // In a real install all heads sit together, so tack-ui is right beside tack. Under a dev run
        // (`dotnet run` / run.bat) they're in separate per-project bin dirs, so fall back to the sibling
        // Tack.App output for the same build configuration.
        string ui = Path.Combine(AppContext.BaseDirectory, exe);
        if (!File.Exists(ui) && FindDevUi(exe) is { } devUi)
            ui = devUi;

        if (!File.Exists(ui))
        {
            AnsiConsole.MarkupLine("[red]tack-ui not found.[/]");
            AnsiConsole.MarkupLine("[grey]In an installed tack it sits beside tack.exe. For a local dev run, build the UI first[/]");
            AnsiConsole.MarkupLine("[grey]([green]dotnet build src/Tack.App[/]) or launch it directly with [green]run-ui.bat[/].[/]");
            return 1;
        }
        try
        {
            Process.Start(new ProcessStartInfo(ui) { UseShellExecute = true });
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]could not launch the UI:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    // From a CLI dev output dir (...\src\Tack.Cli\bin\<Config>\net10.0\) locate the sibling UI build
    // (...\src\Tack.App\bin\<Config>\net10.0-windows\tack-ui.exe). Returns null if the layout doesn't match
    // (e.g. an installed layout, where the beside-path already applied).
    private static string? FindDevUi(string exe)
    {
        try
        {
            string tfmDir = AppContext.BaseDirectory.TrimEnd('\\', '/');       // ...\bin\<Config>\net10.0
            string? configDir = Path.GetDirectoryName(tfmDir);                 // ...\bin\<Config>
            string? binDir = Path.GetDirectoryName(configDir);                 // ...\bin
            string? cliProj = Path.GetDirectoryName(binDir);                   // ...\src\Tack.Cli
            string? srcDir = Path.GetDirectoryName(cliProj);                   // ...\src
            if (configDir is null || srcDir is null) return null;

            string config = Path.GetFileName(configDir);                       // Debug / Release
            string candidate = Path.Combine(srcDir, "Tack.App", "bin", config, "net10.0-windows", exe);
            return File.Exists(candidate) ? candidate : null;
        }
        catch
        {
            return null;
        }
    }
}
