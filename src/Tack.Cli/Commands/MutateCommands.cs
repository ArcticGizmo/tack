using System.ComponentModel;
using System.Text;
using Spectre.Console;
using Spectre.Console.Cli;
using Tack.Core.Config;
using Tack.Core.Installs;
using Tack.Core.Maintenance;
using Tack.Core.Resolution;

namespace Tack.Cli.Commands;

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
        var config = env.Load();

        // Commands installed into managed versions since (npm i -g, pip install) become shims.
        var search = OperatingSystem.IsWindows() ? CommandSearch.Current() : null;
        var rescanned = ManagedCommands.Rescan(config,
            name => search?.WindowsOwner(name) is { } windows ? $"a Windows command ({windows})" : null);
        foreach (var r in rescanned)
        {
            string at = Markup.Escape($"{r.Tool}@{r.Version}");
            if (r.Added.Count > 0)
                AnsiConsole.MarkupLine($"[green]{at} added:[/] {Markup.Escape(string.Join(", ", r.Added))}");
            if (r.Removed.Count > 0)
                AnsiConsole.MarkupLine($"[grey]{at} no longer has:[/] {Markup.Escape(string.Join(", ", r.Removed))}");
            foreach (var (name, why) in r.Skipped)
                AnsiConsole.MarkupLine($"[yellow]{at}: not shimming '{Markup.Escape(name)}':[/] [grey]{Markup.Escape(why)}[/]");
        }
        if (rescanned.Any(r => r.Changed)) env.Save(config);

        // The explicit repair: also refreshes shims left over from an older shim build.
        bool synced = Shims.Sync(env, config, checkPayload: true);
        var added = rescanned.SelectMany(r => r.Added).ToList();
        if (added.Count > 0) ToolsAddCommand.Reach(env, config, added, search);
        return synced ? 0 : 1;
    }
}
