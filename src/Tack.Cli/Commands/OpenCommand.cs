using System.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Tack.Cli.Commands;

public sealed class OpenCommand : Command
{
    public override int Execute(CommandContext context)
    {
        string ui = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "tack-ui.exe" : "tack-ui");
        if (!File.Exists(ui))
        {
            AnsiConsole.MarkupLine($"[red]tack-ui not found next to tack[/] ({Markup.Escape(ui)}).");
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
}
