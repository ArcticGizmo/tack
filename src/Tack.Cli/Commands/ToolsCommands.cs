using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Tack.Core;
using Tack.Core.Config;
using Tack.Core.Maintenance;
using Tack.Core.Resolution;

namespace Tack.Cli.Commands;

// ---- tool add ------------------------------------------------------------------------------------

public sealed class ToolsAddSettings : CommandSettings
{
    [CommandArgument(0, "<tool@version>")]
    [Description("e.g. node@20.11.0")]
    public string Spec { get; init; } = "";

    [CommandArgument(1, "[binDir]")]
    [Description("The directory holding the tool's executables. Omit to discover it from PATH (like 'where').")]
    public string? PositionalBinDir { get; init; }

    [CommandOption("--path <BINDIR>")]
    [Description("Same as the binDir argument, for those who like to spell it out.")]
    public string? PathOption { get; init; }

    /// <summary>The binDir from whichever of the positional or --path was given (empty if neither).</summary>
    public string BinDir => PositionalBinDir is { Length: > 0 } p ? p : PathOption ?? "";

    [CommandOption("--exposes <NAMES>")]
    [Description("Comma-separated binary names; auto-detected from the binDir if omitted.")]
    public string? Exposes { get; init; }

    public override ValidationResult Validate()
    {
        var s = ToolSpec.Parse(Spec);
        if (string.IsNullOrEmpty(s.Tool) || string.IsNullOrEmpty(s.Version))
            return ValidationResult.Error("Specify tool@version, e.g. node@20.11.0");
        if (!string.IsNullOrWhiteSpace(PositionalBinDir) && !string.IsNullOrWhiteSpace(PathOption))
            return ValidationResult.Error("Give the binDir once - either as the second argument or with --path, not both.");
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
            // Relative paths (., ./bin, ../../node) resolve against the cwd; drop any trailing slash so
            // `.` and `.\` register the same binDir. (Roots like C:\ keep theirs.)
            binDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(settings.BinDir));
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
    /// guidance to pass the binDir. Returns null on failure/cancel, with <paramref name="failure"/> the exit code.</summary>
    private static string? DiscoverBinDir(TackEnvironment env, string tool, out int failure)
    {
        failure = 0;
        var matches = PathScan.FindOnPath(
            tool,
            t => Environment.GetEnvironmentVariable("PATH", t),
            // Every tack shims dir (both profiles): a dev instance must not "discover" the release tack's shims.
            TackPaths.AllShimsDirs.Append(env.ShimsDir).Append(env.DisabledShimsDir).Append(env.InstallDir),
            File.Exists);

        if (matches.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]couldn't find '{Markup.Escape(tool)}' on PATH.[/] Pass its install folder: [green]tack tool add {Markup.Escape(tool)}@<version> <binDir>[/].");
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
            AnsiConsole.MarkupLine($"[yellow]'{Markup.Escape(tool)}' was found in several places.[/] Re-run with one as the [green]<binDir>[/] argument:");
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

// ---- tool list -----------------------------------------------------------------------------------

public sealed class ToolsListSettings : CommandSettings
{
    [CommandOption("-e|--expand")]
    [Description("List each version as plain lines instead of a table, so paths never wrap and copy cleanly.")]
    public bool Expand { get; init; }
}

public sealed class ToolsListCommand : Command<ToolsListSettings>
{
    public override int Execute(CommandContext context, ToolsListSettings settings)
    {
        var env = new TackEnvironment();
        var config = env.Load();
        if (config.Tools.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No tools registered.[/] Use [green]tack tool add[/] to add one.");
            return 0;
        }

        var resolver = env.ResolverFor(config);
        var ctx = env.Context();
        string cwd = Environment.CurrentDirectory;

        // One entry per version, so each install's own directory is visible.
        var rows = new List<Row>();
        foreach (var (name, tool) in config.Tools.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            string? def = config.Defaults.TryGetValue(name, out var d) ? d : null;
            var r = resolver.Resolve(name, cwd, ctx);
            string? here = r.Resolved ? r.Version : null;

            foreach (var (version, installed) in tool.Versions.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase))
                rows.Add(new Row(name, version, installed.BinDir,
                    // The command names tack intercepts for this version.
                    string.Join(", ", installed.Exposes.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
                    IsDefault: string.Equals(version, def, StringComparison.OrdinalIgnoreCase),
                    IsHere: string.Equals(version, here, StringComparison.OrdinalIgnoreCase),
                    Missing: !Directory.Exists(installed.BinDir)));
        }

        if (settings.Expand) WriteExpanded(rows);
        else WriteTable(rows);

        if (rows.Any(x => x.Missing))
            AnsiConsole.MarkupLine("[yellow]note:[/] a registered folder is missing - re-add it with [green]tack tool add <tool@version> <binDir>[/], or remove it with [green]tack tool remove[/].");
        return 0;
    }

    private sealed record Row(string Tool, string Version, string BinDir, string Commands,
        bool IsDefault, bool IsHere, bool Missing);

    /// <summary>The compact view. The tool name is shown on its first row only; the version cell carries the
    /// default / resolves-here markers. Long paths wrap inside their cell - <c>--expand</c> is for copying.</summary>
    private static void WriteTable(List<Row> rows)
    {
        var table = new Table().RoundedBorder();
        table.AddColumn("tool");
        table.AddColumn(new TableColumn("version").NoWrap());
        table.AddColumn("path");
        table.AddColumn("commands");
        string? previous = null;
        foreach (var x in rows)
        {
            string versionCell = Markup.Escape(x.Version)
                + (x.IsDefault ? " [grey]default[/]" : "")
                + (x.IsHere ? " [green]here[/]" : "");
            string pathCell = x.Missing
                ? $"[red]{Markup.Escape(x.BinDir)} (missing)[/]"
                : Markup.Escape(x.BinDir);
            table.AddRow(x.Tool == previous ? "" : Markup.Escape(x.Tool), versionCell, pathCell,
                $"[grey]{Markup.Escape(x.Commands)}[/]");
            previous = x.Tool;
        }
        AnsiConsole.Write(table);
    }

    /// <summary>The copy-friendly view: a block of plain lines per version. The path line goes straight to stdout
    /// rather than through Spectre, which hard-wraps at the console width - the terminal's own soft wrap copies
    /// back as one line.</summary>
    private static void WriteExpanded(List<Row> rows)
    {
        bool first = true;
        foreach (var x in rows)
        {
            if (!first) Console.WriteLine();
            first = false;
            AnsiConsole.MarkupLine($"[bold]{Markup.Escape(x.Tool)}@{Markup.Escape(x.Version)}[/]"
                + (x.IsDefault ? " [grey]default[/]" : "")
                + (x.IsHere ? " [green]here[/]" : "")
                + (x.Missing ? " [red]missing[/]" : ""));
            Console.WriteLine($"  path:     {x.BinDir}");
            Console.WriteLine($"  commands: {x.Commands}");
        }
    }
}

// ---- tool remove ---------------------------------------------------------------------------------

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
        foreach (var z in result.OrphanedZones)
            AnsiConsole.MarkupLine($"[yellow]zone now points at a removed version:[/] {Markup.Escape(z)} [grey](edit with tack zone)[/]");

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
