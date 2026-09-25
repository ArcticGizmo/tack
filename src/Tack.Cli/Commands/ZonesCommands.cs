using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Tack.Core.Config;
using Tack.Core.Resolution;

namespace Tack.Cli.Commands;

// ---- zone add ------------------------------------------------------------------------------------

public sealed class ZonesAddSettings : CommandSettings
{
    [CommandArgument(0, "<dir>")]
    [Description("The directory the zone covers, and everything under it. Relative paths (like .) resolve from here.")]
    public string Dir { get; init; } = "";

    [CommandArgument(1, "<tool@version>")]
    [Description("e.g. node@18.19.0 (a prefix like node@18 picks the highest registered 18.x), node@none to turn tack off for node here, or just none to turn it off for every tool")]
    public string Spec { get; init; } = "";

    [CommandOption("--enforce")]
    [Description("Make this zone beat a repo tack.yml (org enforcement).")]
    public bool Enforce { get; init; }

    public override ValidationResult Validate()
    {
        if (string.IsNullOrWhiteSpace(Dir)) return ValidationResult.Error("A directory is required");
        if (Dir.Contains('*') || Dir.Contains('?'))
            return ValidationResult.Error("Zones take a plain directory, not a glob - everything under it is already included");
        var s = ParseSpec(Spec);
        if (string.IsNullOrEmpty(s.Tool) || string.IsNullOrEmpty(s.Version))
            return ValidationResult.Error("Specify tool@version (e.g. node@18.19.0), or none to turn tack off for every tool");
        if (ZoneTool.IsAll(s.Tool) && !ZoneVersion.IsNone(s.Version))
            return ValidationResult.Error("A zone for every tool can only be none - one version can't fit every tool");
        return ValidationResult.Success();
    }

    /// <summary>Like <see cref="ToolSpec.Parse"/>, plus a bare <c>none</c> meaning "every tool, none" (<c>*@none</c>).</summary>
    public static ToolSpec ParseSpec(string spec) =>
        ZoneVersion.IsNone(spec) ? new ToolSpec(ZoneTool.All, ZoneVersion.None) : ToolSpec.Parse(spec);
}

public sealed class ZonesAddCommand : Command<ZonesAddSettings>
{
    public override int Execute(CommandContext context, ZonesAddSettings settings)
    {
        var env = new TackEnvironment();
        var spec = ZonesAddSettings.ParseSpec(settings.Spec);
        string dir = Path.GetFullPath(settings.Dir);
        bool none = ZoneVersion.IsNone(spec.Version);
        bool all = ZoneTool.IsAll(spec.Tool);
        string version = none ? ZoneVersion.None : spec.Version!;
        var config = env.Load();

        var result = ZoneRegistry.Set(config, dir, spec.Tool, version, settings.Enforce);
        env.Save(config);

        var zone = new Zone { Path = dir, Tool = spec.Tool, Version = version };
        string what = $"{Markup.Escape(dir)} -> {Markup.Escape(ZoneText.Spec(zone))}"
            + (settings.Enforce ? " [red](enforced)[/]" : "");
        if (result.Previous is { } p)
            AnsiConsole.MarkupLine($"[green]updated zone[/] {what} [grey](was {Markup.Escape(p.Version)}{(p.Enforce ? ", enforced" : "")})[/]");
        else
            AnsiConsole.MarkupLine($"[green]added zone[/] {what}");

        if (none)
        {
            string subject = all ? "any tool" : spec.Tool;
            string overrides = all
                ? (settings.Enforce ? "a tool's own enforced zone" : "a tool's own zone or a tack.yml")
                : (settings.Enforce ? "a deeper enforced zone" : "a deeper zone or a tack.yml");
            AnsiConsole.MarkupLine($"[grey]tack won't resolve {Markup.Escape(subject)} here or below (unless {overrides} says otherwise) - commands go to the next one on PATH.[/]");
        }
        if (!Directory.Exists(dir))
            AnsiConsole.MarkupLine("[yellow]note:[/] that directory doesn't exist yet - the zone applies once it does.");
        // An all-tools zone covers whatever is registered, now or later - nothing to check.
        if (!all) NoteRegistration(config, spec.Tool, version, none);

        Mutations.ReportReshim(env.Reshim(config));
        return 0;
    }

    private static void NoteRegistration(CentralConfig config, string toolName, string version, bool none)
    {
        if (!config.Tools.TryGetValue(toolName, out var tool))
            AnsiConsole.MarkupLine($"[yellow]note:[/] '{Markup.Escape(toolName)}' isn't registered, so this zone does nothing until it is ([green]tack tool add[/]).");
        else if (!none && VersionMatch.Best(tool.Versions.Keys, version) is null)
            AnsiConsole.MarkupLine($"[yellow]note:[/] no registered {Markup.Escape(toolName)} version matches '{Markup.Escape(version)}' - commands here will fail until one is added.");
    }
}

// ---- zone list -----------------------------------------------------------------------------------

public sealed class ZonesListCommand : Command
{
    public override int Execute(CommandContext context)
    {
        var config = new TackEnvironment().Load();
        var zones = ZoneRegistry.Sorted(config);
        if (zones.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No zones.[/] Use [green]tack zone add <dir> tool@version[/] to add one.");
            return 0;
        }

        var table = new Table().RoundedBorder();
        table.AddColumn("directory");
        table.AddColumn("tool");
        table.AddColumn("version");
        table.AddColumn("enforced");
        foreach (var z in zones)
            table.AddRow(Markup.Escape(z.Path),
                ZoneTool.IsAll(z.Tool) ? "[yellow]all tools[/]" : Markup.Escape(z.Tool),
                ZoneVersion.IsNone(z.Version) ? "[yellow]none[/] [grey](tack off)[/]" : Markup.Escape(z.Version),
                z.Enforce ? "[red]yes[/]" : "");
        AnsiConsole.Write(table);
        return 0;
    }
}

// ---- zone remove ---------------------------------------------------------------------------------

public sealed class ZonesRemoveSettings : CommandSettings
{
    [CommandArgument(0, "[dir]")]
    [Description("The zone's directory. Omit for an interactive picker.")]
    public string? Dir { get; init; }

    [CommandArgument(1, "[tool]")]
    [Description("Remove just this tool's zone at the directory (* or none for the all-tools zone). Omit to remove every zone there.")]
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
            // `none` mirrors `zone add <dir> none`, and spares bash users from quoting a `*`.
            string? tool = string.IsNullOrWhiteSpace(settings.Tool) ? null
                : ZoneVersion.IsNone(settings.Tool) ? ZoneTool.All
                : settings.Tool;
            removed = ZoneRegistry.Remove(config, dir, tool);
            if (removed.Count == 0)
            {
                string which = string.IsNullOrWhiteSpace(settings.Tool) ? "" : $" for '{settings.Tool}'";
                AnsiConsole.MarkupLine($"[red]no zone at {Markup.Escape(dir)}{Markup.Escape(which)}.[/] See [green]tack zone list[/].");
                return 1;
            }
        }

        env.Save(config);
        foreach (var z in removed)
            AnsiConsole.MarkupLine($"[green]removed zone[/] {Markup.Escape(z.Path)} -> {Markup.Escape(ZoneText.Spec(z))}");
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
        $"{z.Path}  {ZoneText.Spec(z)}{(z.Enforce ? "  (enforced)" : "")}";
}

internal static class ZoneText
{
    /// <summary>A zone's tool@version for messages: <c>node@18</c>, or <c>none (all tools)</c> for a <c>*</c> zone.</summary>
    public static string Spec(Zone z) =>
        ZoneTool.IsAll(z.Tool) ? $"{z.Version} (all tools)" : $"{z.Tool}@{z.Version}";
}
