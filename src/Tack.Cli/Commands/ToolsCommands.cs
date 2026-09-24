using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Tack.Core.Config;
using Tack.Core.Maintenance;
using Tack.Core.Resolution;

namespace Tack.Cli.Commands;

// ---- tools add -----------------------------------------------------------------------------------

public sealed class ToolsAddSettings : CommandSettings
{
    [CommandArgument(0, "<tool@version>")]
    [Description("e.g. node@20.11.0")]
    public string Spec { get; init; } = "";

    [CommandOption("--path <BINDIR>")]
    [Description("The directory holding the tool's executables. Omit to discover it from PATH (like 'where').")]
    public string BinDir { get; init; } = "";

    [CommandOption("--exposes <NAMES>")]
    [Description("Comma-separated binary names; auto-detected from the binDir if omitted.")]
    public string? Exposes { get; init; }

    public override ValidationResult Validate()
    {
        var s = ToolSpec.Parse(Spec);
        if (string.IsNullOrEmpty(s.Tool) || string.IsNullOrEmpty(s.Version))
            return ValidationResult.Error("Specify tool@version, e.g. node@20.11.0");
        return ValidationResult.Success();
    }
}

public sealed class ToolsAddCommand : Command<ToolsAddSettings>
{
    public override int Execute(CommandContext context, ToolsAddSettings settings)
    {
        var env = new TackEnvironment();
        var spec = ToolSpec.Parse(settings.Spec);

        string? binDir;
        if (string.IsNullOrWhiteSpace(settings.BinDir))
        {
            binDir = DiscoverBinDir(env, spec.Tool, out int failure);
            if (binDir is null) return failure;
        }
        else
        {
            binDir = Path.GetFullPath(settings.BinDir);
        }

        if (!Directory.Exists(binDir))
        {
            AnsiConsole.MarkupLine($"[red]binDir does not exist:[/] {Markup.Escape(binDir)}");
            return 1;
        }

        var exposes = settings.Exposes is { Length: > 0 } e
            ? e.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            : ToolProbe.DetectExposes(binDir);
        if (!exposes.Any(x => string.Equals(x, spec.Tool, StringComparison.OrdinalIgnoreCase)))
            exposes.Insert(0, spec.Tool); // the tool's own name must be shimmed

        var config = env.Load();
        if (!config.Tools.TryGetValue(spec.Tool, out var tool))
        {
            tool = new RegisteredTool();
            config.Tools[spec.Tool] = tool;
        }
        tool.Versions[spec.Version!] = new InstalledVersion { BinDir = binDir, Exposes = exposes };
        if (!config.Defaults.ContainsKey(spec.Tool))
            config.Defaults[spec.Tool] = spec.Version!;

        env.Save(config);
        AnsiConsole.MarkupLine($"[green]added[/] {Markup.Escape(spec.Tool)}@{Markup.Escape(spec.Version!)} -> {Markup.Escape(binDir)}");
        AnsiConsole.MarkupLine($"[grey]exposes:[/] {Markup.Escape(string.Join(", ", exposes))}");
        Mutations.ReportReshim(env.Reshim(config));
        if (env.IsDisabled)
            AnsiConsole.MarkupLine("[yellow]note:[/] tack is disabled - this is configured but won't take effect until [green]tack enable[/].");
        else if (!Render.OnPath(env.ShimsDir))
            AnsiConsole.MarkupLine("[yellow]note:[/] the shims dir is not on PATH yet - installing tack wires it up, or run [green]tack doctor[/].");
        return 0;
    }

    /// <summary>Discover the tool's binDir from PATH (like `where`), excluding tack's own dirs. One hit is used
    /// directly; several open an interactive pick; none (or a non-interactive terminal with several) fails with
    /// guidance to pass --path. Returns null on failure/cancel, with <paramref name="failure"/> the exit code.</summary>
    private static string? DiscoverBinDir(TackEnvironment env, string tool, out int failure)
    {
        failure = 0;
        var matches = PathScan.FindOnPath(
            tool,
            t => Environment.GetEnvironmentVariable("PATH", t),
            new[] { env.ShimsDir, env.DisabledShimsDir, env.InstallDir },
            File.Exists);

        if (matches.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]couldn't find '{Markup.Escape(tool)}' on PATH.[/] Pass [green]--path <binDir>[/] to point at its install.");
            failure = 1;
            return null;
        }

        if (matches.Count == 1)
        {
            AnsiConsole.MarkupLine($"[grey]found on PATH:[/] {Markup.Escape(matches[0].ExePath)}");
            return matches[0].BinDir;
        }

        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            AnsiConsole.MarkupLine($"[yellow]'{Markup.Escape(tool)}' was found in several places.[/] Re-run with [green]--path <binDir>[/]:");
            foreach (var m in matches)
                AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(m.ExePath)}[/]");
            failure = 1;
            return null;
        }

        // Map each display line back to its binDir; show the full exe path so identical dir names stay distinct.
        var byLine = matches.ToDictionary(m => m.ExePath, m => m.BinDir);
        var choice = AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title($"Which [green]{Markup.Escape(tool)}[/] should tack register?")
            .PageSize(15)
            .MoreChoicesText("[grey](move up and down to reveal more)[/]")
            .AddChoices(byLine.Keys));
        return byLine[choice];
    }
}

// ---- tools list ----------------------------------------------------------------------------------

public sealed class ToolsListCommand : Command
{
    public override int Execute(CommandContext context)
    {
        var env = new TackEnvironment();
        var config = env.Load();
        if (config.Tools.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No tools registered.[/] Use [green]tack tools add[/] to add one.");
            return 0;
        }

        var resolver = env.ResolverFor(config);
        var ctx = env.Context();
        string cwd = Environment.CurrentDirectory;

        var table = new Table().RoundedBorder();
        table.AddColumn("tool");
        table.AddColumn("versions");
        table.AddColumn("default");
        table.AddColumn("here");
        table.AddColumn("commands");
        foreach (var (name, tool) in config.Tools.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            string versions = string.Join(", ", tool.Versions.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            string def = config.Defaults.TryGetValue(name, out var d) ? d : "-";
            var r = resolver.Resolve(name, cwd, ctx);
            string here = r.Resolved ? r.Version! : "-";
            // The command names tack intercepts for this tool (union across versions).
            string commands = string.Join(", ", tool.Versions.Values
                .SelectMany(v => v.Exposes)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            table.AddRow(Markup.Escape(name), Markup.Escape(versions), Markup.Escape(def), Markup.Escape(here),
                $"[grey]{Markup.Escape(commands)}[/]");
        }
        AnsiConsole.Write(table);
        return 0;
    }
}

// ---- tools remove --------------------------------------------------------------------------------

public sealed class ToolsRemoveSettings : CommandSettings
{
    [CommandArgument(0, "[tool@version]")]
    [Description("The tool (or tool@version) to remove. Omit for an interactive picker; give a tool with several versions to pick from just its versions.")]
    public string? Spec { get; init; }
}

public sealed class ToolsRemoveCommand : Command<ToolsRemoveSettings>
{
    public override int Execute(CommandContext context, ToolsRemoveSettings settings)
    {
        var env = new TackEnvironment();
        var config = env.Load();
        if (config.Tools.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No tools registered.[/] Nothing to remove.");
            return 0;
        }

        var targets = ChooseTargets(config, settings.Spec, out int failure);
        if (targets is null) return failure;
        if (targets.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]nothing selected; no changes.[/]");
            return 0;
        }

        var result = ToolRegistry.Remove(config, targets.Select(e => Split(e)));
        env.Save(config);

        AnsiConsole.MarkupLine($"[green]removed[/] {Markup.Escape(string.Join(", ", result.Removed))}");
        foreach (var t in result.ToolsDropped)
            AnsiConsole.MarkupLine($"[grey]dropped '{Markup.Escape(t)}' (no versions left)[/]");
        foreach (var r in result.DefaultsRepointed)
            AnsiConsole.MarkupLine($"[grey]default repointed:[/] {Markup.Escape(r)}");
        foreach (var b in result.OrphanedBindings)
            AnsiConsole.MarkupLine($"[yellow]binding now points at a removed version:[/] {Markup.Escape(b)} [grey](edit with tack bind / the UI)[/]");

        Mutations.ReportReshim(env.Reshim(config));
        return 0;
    }

    /// <summary>Work out which tool@version entries to remove, from the argument and/or an interactive picker.
    /// Returns null on an error (with <paramref name="failure"/> the exit code), or the (possibly empty) list.</summary>
    private static List<string>? ChooseTargets(CentralConfig config, string? spec, out int failure)
    {
        failure = 0;
        var parsed = string.IsNullOrWhiteSpace(spec) ? (ToolSpec?)null : ToolSpec.Parse(spec);

        // Fully specified tool@version - remove exactly that.
        if (parsed is { } s && !string.IsNullOrEmpty(s.Version))
        {
            if (!ToolRegistry.Exists(config, s.Tool, s.Version))
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(s.Tool)}@{Markup.Escape(s.Version!)} is not registered.[/]");
                failure = 1;
                return null;
            }
            return new List<string> { $"{s.Tool}@{s.Version}" };
        }

        // A bare tool: one version -> remove it directly; several -> pick from just that tool's versions.
        List<string> candidates;
        if (parsed is { } t && !string.IsNullOrEmpty(t.Tool))
        {
            var versions = ToolRegistry.VersionsOf(config, t.Tool);
            if (versions.Count == 0)
            {
                AnsiConsole.MarkupLine($"[red]'{Markup.Escape(t.Tool)}' is not registered.[/]");
                failure = 1;
                return null;
            }
            if (versions.Count == 1)
                return new List<string> { $"{t.Tool}@{versions[0]}" };
            candidates = versions.Select(v => $"{t.Tool}@{v}").ToList();
        }
        else
        {
            candidates = ToolRegistry.Entries(config);
        }

        return PickInteractively(candidates);
    }

    private static List<string>? PickInteractively(List<string> candidates)
    {
        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            AnsiConsole.MarkupLine("[yellow]not an interactive terminal.[/] Re-run with an explicit [green]tool@version[/]:");
            foreach (var c in candidates)
                AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(c)}[/]");
            return new List<string>(); // treated as "nothing selected" -> no changes, exit 0
        }

        var prompt = new MultiSelectionPrompt<string>()
            .Title("Select [red]tool versions[/] to remove")
            .NotRequired()
            .PageSize(15)
            .WrapAround()
            .MoreChoicesText("[grey](move up and down to reveal more)[/]")
            .InstructionsText("[grey](press [blue]<space>[/] to toggle, [green]<enter>[/] to confirm - nothing selected cancels)[/]")
            .AddChoices(candidates);

        return AnsiConsole.Prompt(prompt);
    }

    private static (string Tool, string Version) Split(string entry)
    {
        var s = ToolSpec.Parse(entry);
        return (s.Tool, s.Version ?? "");
    }
}
