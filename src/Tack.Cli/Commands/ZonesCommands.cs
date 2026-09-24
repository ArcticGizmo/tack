using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Tack.Core.Config;
using Tack.Core.Resolution;

namespace Tack.Cli.Commands;

// ---- zones add -----------------------------------------------------------------------------------

public sealed class ZonesAddSettings : CommandSettings
{
    [CommandArgument(0, "<dir>")]
    [Description("The directory the zone covers, and everything under it. Relative paths (like .) resolve from here.")]
    public string Dir { get; init; } = "";

    [CommandArgument(1, "<tool@version>")]
    [Description("e.g. node@18.19.0 (a prefix like node@18 picks the highest registered 18.x)")]
    public string Spec { get; init; } = "";

    [CommandOption("--enforce")]
    [Description("Make this zone beat a repo tack.yml (org enforcement).")]
    public bool Enforce { get; init; }

    public override ValidationResult Validate()
    {
        if (string.IsNullOrWhiteSpace(Dir)) return ValidationResult.Error("A directory is required");
        if (Dir.Contains('*') || Dir.Contains('?'))
            return ValidationResult.Error("Zones take a plain directory, not a glob - everything under it is already included");
        var s = ToolSpec.Parse(Spec);
        if (string.IsNullOrEmpty(s.Tool) || string.IsNullOrEmpty(s.Version))
            return ValidationResult.Error("Specify tool@version, e.g. node@18.19.0");
        return ValidationResult.Success();
    }
}

public sealed class ZonesAddCommand : Command<ZonesAddSettings>
{
    public override int Execute(CommandContext context, ZonesAddSettings settings)
    {
        var env = new TackEnvironment();
        var spec = ToolSpec.Parse(settings.Spec);
        string dir = Path.GetFullPath(settings.Dir);
        var config = env.Load();

        var result = ZoneRegistry.Set(config, dir, spec.Tool, spec.Version!, settings.Enforce);
        env.Save(config);

        string what = $"{Markup.Escape(dir)} -> {Markup.Escape(spec.Tool)}@{Markup.Escape(spec.Version!)}"
            + (settings.Enforce ? " [red](enforced)[/]" : "");
        if (result.Previous is { } p)
            AnsiConsole.MarkupLine($"[green]updated zone[/] {what} [grey](was {Markup.Escape(p.Version)}{(p.Enforce ? ", enforced" : "")})[/]");
        else
            AnsiConsole.MarkupLine($"[green]added zone[/] {what}");

        if (!Directory.Exists(dir))
            AnsiConsole.MarkupLine("[yellow]note:[/] that directory doesn't exist yet - the zone applies once it does.");
        if (!config.Tools.TryGetValue(spec.Tool, out var tool))
            AnsiConsole.MarkupLine($"[yellow]note:[/] '{Markup.Escape(spec.Tool)}' isn't registered, so this zone does nothing until it is ([green]tack tools add[/]).");
        else if (VersionMatch.Best(tool.Versions.Keys, spec.Version!) is null)
            AnsiConsole.MarkupLine($"[yellow]note:[/] no registered {Markup.Escape(spec.Tool)} version matches '{Markup.Escape(spec.Version!)}' - commands here will fail until one is added.");

        Mutations.ReportReshim(env.Reshim(config));
        return 0;
    }
}

// ---- zones list ----------------------------------------------------------------------------------

public sealed class ZonesListCommand : Command
{
    public override int Execute(CommandContext context)
    {
        var config = new TackEnvironment().Load();
        var zones = ZoneRegistry.Sorted(config);
        if (zones.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No zones.[/] Use [green]tack zones add <dir> tool@version[/] to add one.");
            return 0;
        }

        var table = new Table().RoundedBorder();
        table.AddColumn("directory");
        table.AddColumn("tool");
        table.AddColumn("version");
        table.AddColumn("enforced");
        foreach (var z in zones)
            table.AddRow(Markup.Escape(z.Path), Markup.Escape(z.Tool), Markup.Escape(z.Version),
                z.Enforce ? "[red]yes[/]" : "");
        AnsiConsole.Write(table);
        return 0;
    }
}

// ---- zones remove --------------------------------------------------------------------------------

public sealed class ZonesRemoveSettings : CommandSettings
{
    [CommandArgument(0, "[dir]")]
    [Description("The zone's directory. Omit for an interactive picker.")]
    public string? Dir { get; init; }

    [CommandArgument(1, "[tool]")]
    [Description("Remove just this tool's zone at the directory. Omit to remove every tool's zone there.")]
    public string? Tool { get; init; }
}

public sealed class ZonesRemoveCommand : Command<ZonesRemoveSettings>
{
    public override int Execute(CommandContext context, ZonesRemoveSettings settings)
    {
        var env = new TackEnvironment();
        var config = env.Load();
        if (config.Zones.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No zones.[/] Nothing to remove.");
            return 0;
        }

        var removed = new List<Zone>();
        if (string.IsNullOrWhiteSpace(settings.Dir))
        {
            var picked = PickInteractively(ZoneRegistry.Sorted(config));
            if (picked.Count == 0)
            {
                AnsiConsole.MarkupLine("[grey]nothing selected; no changes.[/]");
                return 0;
            }
            foreach (var z in picked)
                removed.AddRange(ZoneRegistry.Remove(config, z.Path, z.Tool));
        }
        else
        {
            string dir = Path.GetFullPath(settings.Dir);
            removed = ZoneRegistry.Remove(config, dir, string.IsNullOrWhiteSpace(settings.Tool) ? null : settings.Tool);
            if (removed.Count == 0)
            {
                string which = string.IsNullOrWhiteSpace(settings.Tool) ? "" : $" for '{settings.Tool}'";
                AnsiConsole.MarkupLine($"[red]no zone at {Markup.Escape(dir)}{Markup.Escape(which)}.[/] See [green]tack zones list[/].");
                return 1;
            }
        }

        env.Save(config);
        foreach (var z in removed)
            AnsiConsole.MarkupLine($"[green]removed zone[/] {Markup.Escape(z.Path)} -> {Markup.Escape(z.Tool)}@{Markup.Escape(z.Version)}");
        Mutations.ReportReshim(env.Reshim(config));
        return 0;
    }

    private static List<Zone> PickInteractively(List<Zone> zones)
    {
        var byLine = zones.ToDictionary(Label);
        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            AnsiConsole.MarkupLine("[yellow]not an interactive terminal.[/] Re-run with an explicit [green]<dir> [[tool]][/]:");
            foreach (var line in byLine.Keys)
                AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(line)}[/]");
            return new List<Zone>(); // treated as "nothing selected" -> no changes, exit 0
        }

        var prompt = new MultiSelectionPrompt<string>()
            .Title("Select [red]zones[/] to remove")
            .NotRequired()
            .PageSize(15)
            .WrapAround()
            .MoreChoicesText("[grey](move up and down to reveal more)[/]")
            .InstructionsText("[grey](press [blue]<space>[/] to toggle, [green]<enter>[/] to confirm - nothing selected cancels)[/]")
            .UseConverter(Markup.Escape)
            .AddChoices(byLine.Keys);

        return AnsiConsole.Prompt(prompt).Select(line => byLine[line]).ToList();
    }

    private static string Label(Zone z) =>
        $"{z.Path}  {z.Tool}@{z.Version}{(z.Enforce ? "  (enforced)" : "")}";
}
