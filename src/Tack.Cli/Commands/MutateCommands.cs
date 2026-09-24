using System.ComponentModel;
using System.Text;
using Spectre.Console;
using Spectre.Console.Cli;
using Tack.Core.Config;
using Tack.Core.Maintenance;

namespace Tack.Cli.Commands;

internal static class Mutations
{
    public static void ReportReshim(ReshimResult r)
    {
        if (r.ShimPayloadMissing)
            AnsiConsole.MarkupLine("[yellow]resolved.json updated, but tack-shim.exe was not found next to tack, so no shims were stamped (expected under a dev `dotnet run`).[/]");
        else
            AnsiConsole.MarkupLine($"[grey]reshim:[/] {r.ShimsWritten} shim(s) written, {r.ShimsPruned} pruned"
                + (r.ShimNames.Count > 0 ? $" [grey]({Markup.Escape(string.Join(", ", r.ShimNames))})[/]" : ""));
        foreach (var glob in r.UnmigratedBindings)
            AnsiConsole.MarkupLine($"[yellow]old binding '{Markup.Escape(glob)}' no longer applies[/] [grey](zones take a plain directory; see tack doctor)[/]");
    }
}

// ---- use -----------------------------------------------------------------------------------------

public sealed class UseSettings : CommandSettings
{
    [CommandArgument(0, "<tool[@version]>")]
    [Description("Pin a tool in this directory's tack.yml. Version defaults to the registered default/highest.")]
    public string Spec { get; init; } = "";
}

public sealed class UseCommand : Command<UseSettings>
{
    public override int Execute(CommandContext context, UseSettings settings)
    {
        var env = new TackEnvironment();
        var spec = ToolSpec.Parse(settings.Spec);
        if (string.IsNullOrEmpty(spec.Tool))
        {
            AnsiConsole.MarkupLine("[red]Specify a tool, e.g. tack use node@20.11.0[/]");
            return 1;
        }

        var config = env.Load();
        string? version = spec.Version ?? PickVersion(config, spec.Tool);
        if (version is null)
        {
            AnsiConsole.MarkupLine($"[red]No version given and none registered for '{Markup.Escape(spec.Tool)}'.[/] Specify tool@version.");
            return 1;
        }

        string path = Path.Combine(Environment.CurrentDirectory, "tack.yml");
        var tools = File.Exists(path)
            ? MiniTackYml.Parse(File.ReadAllText(path)).Tools
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        tools[spec.Tool] = version;
        WriteTackYml(path, tools);

        AnsiConsole.MarkupLine($"[green]pinned[/] {Markup.Escape(spec.Tool)}@{Markup.Escape(version)} in {Markup.Escape(path)}");
        return 0;
    }

    private static string? PickVersion(CentralConfig config, string tool)
    {
        if (config.Defaults.TryGetValue(tool, out var d)) return d;
        if (config.Tools.TryGetValue(tool, out var t) && t.Versions.Count > 0)
            return t.Versions.Keys.OrderByDescending(v => v, StringComparer.OrdinalIgnoreCase).First();
        return null;
    }

    private static void WriteTackYml(string path, Dictionary<string, string> tools)
    {
        var sb = new StringBuilder();
        sb.Append("tools:\n");
        foreach (var (k, v) in tools.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            sb.Append($"  {k}: {v}\n");
        File.WriteAllText(path, sb.ToString());
    }
}

// ---- reshim --------------------------------------------------------------------------------------

public sealed class ReshimCommand : Command
{
    public override int Execute(CommandContext context)
    {
        var env = new TackEnvironment();
        Mutations.ReportReshim(env.Reshim(env.Load()));
        return 0;
    }
}
