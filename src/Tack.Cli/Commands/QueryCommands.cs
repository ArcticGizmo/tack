using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Tack.Core.Resolution;

namespace Tack.Cli.Commands;

// ---- info ----------------------------------------------------------------------------------------

public sealed class InfoSettings : CommandSettings
{
    [CommandArgument(0, "[tool]")]
    [Description("A specific tool to explain; omit for a table of every registered tool.")]
    public string? Tool { get; init; }
}

public sealed class InfoCommand : Command<InfoSettings>
{
    public override int Execute(CommandContext context, InfoSettings settings)
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

        if (settings.Tool is { Length: > 0 } tool)
        {
            if (!config.Tools.ContainsKey(tool))
            {
                AnsiConsole.MarkupLine($"[red]'{Markup.Escape(tool)}' is not registered.[/]");
                return 1;
            }

            var r = resolver.Resolve(tool, cwd, ctx);
            var tree = new Tree($"[bold]{Markup.Escape(tool)}[/] in {Markup.Escape(cwd)}");
            tree.AddNode($"resolves to: [green]{Markup.Escape(Render.Describe(r))}[/]");
            tree.AddNode($"source: {Render.SourceMarkup(r.Source)} [grey]{Markup.Escape(r.Detail ?? "")}[/]");
            if (r.Resolved && r.BinDir is not null)
            {
                string? target = BinaryLocator.Locate(r.BinDir, tool, File.Exists);
                tree.AddNode($"binary: {Markup.Escape(target ?? $"(no '{tool}' in {r.BinDir})")}");
            }
            AnsiConsole.Write(tree);
            return 0;
        }

        var table = new Table().RoundedBorder();
        table.AddColumn("tool");
        table.AddColumn("resolves to");
        table.AddColumn("source");
        table.AddColumn("why");
        foreach (var name in config.Tools.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var r = resolver.Resolve(name, cwd, ctx);
            table.AddRow(Markup.Escape(name), Markup.Escape(Render.Describe(r)),
                Render.SourceMarkup(r.Source), Markup.Escape(r.Detail ?? ""));
        }
        AnsiConsole.MarkupLine($"[grey]directory:[/] {Markup.Escape(cwd)}");
        AnsiConsole.Write(table);
        return 0;
    }
}

// ---- which ---------------------------------------------------------------------------------------

public sealed class WhichSettings : CommandSettings
{
    [CommandArgument(0, "<tool>")]
    public string Tool { get; init; } = "";
}

public sealed class WhichCommand : Command<WhichSettings>
{
    public override int Execute(CommandContext context, WhichSettings settings)
    {
        var env = new TackEnvironment();
        var r = env.ResolverFor(env.Load()).Resolve(settings.Tool, Environment.CurrentDirectory, env.Context());
        if (!r.Resolved || r.BinDir is null)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(settings.Tool)} does not resolve here ({r.Source}).[/]");
            return 1;
        }
        string? target = BinaryLocator.Locate(r.BinDir, settings.Tool, File.Exists);
        if (target is null)
        {
            AnsiConsole.MarkupLine($"[red]no '{Markup.Escape(settings.Tool)}' binary in {Markup.Escape(r.BinDir)}[/]");
            return 1;
        }
        Console.WriteLine(target); // plain + scriptable, like `mise which`
        return 0;
    }
}

// ---- shims ---------------------------------------------------------------------------------------

public sealed class ShimsCommand : Command
{
    public override int Execute(CommandContext context)
    {
        var env = new TackEnvironment();
        string dir = env.ActiveShimsDir;
        AnsiConsole.MarkupLine($"[grey]shims dir:[/] {Markup.Escape(dir)}");

        if (env.IsDisabled)
            AnsiConsole.MarkupLine("[yellow]tack is disabled[/] - shims are parked here; run [green]tack enable[/] to go live.");

        if (!Directory.Exists(dir))
        {
            AnsiConsole.MarkupLine("[yellow]shims dir does not exist yet - run [green]tack reshim[/].[/]");
            return 0;
        }

        var exes = Directory.GetFiles(dir, "*.exe")
            .Select(Path.GetFileNameWithoutExtension)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        AnsiConsole.MarkupLine(exes.Count == 0
            ? "[yellow]no shims generated.[/]"
            : "shims: " + string.Join(", ", exes.Select(x => Markup.Escape(x!))));

        if (!env.IsDisabled)
            AnsiConsole.MarkupLine(Render.OnPath(env.ShimsDir)
                ? "[green]shims dir is on PATH.[/]"
                : "[red]shims dir is NOT on PATH[/] - run [green]tack doctor[/].");
        return 0;
    }
}
