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
    }
}

// ---- register ------------------------------------------------------------------------------------

public sealed class RegisterSettings : CommandSettings
{
    [CommandArgument(0, "<tool@version>")]
    [Description("e.g. node@20.11.0")]
    public string Spec { get; init; } = "";

    [CommandOption("--path <BINDIR>")]
    [Description("The directory holding the tool's executables.")]
    public string BinDir { get; init; } = "";

    [CommandOption("--exposes <NAMES>")]
    [Description("Comma-separated binary names; auto-detected from the binDir if omitted.")]
    public string? Exposes { get; init; }

    public override ValidationResult Validate()
    {
        var s = ToolSpec.Parse(Spec);
        if (string.IsNullOrEmpty(s.Tool) || string.IsNullOrEmpty(s.Version))
            return ValidationResult.Error("Specify tool@version, e.g. node@20.11.0");
        if (string.IsNullOrWhiteSpace(BinDir))
            return ValidationResult.Error("--path <binDir> is required");
        return ValidationResult.Success();
    }
}

public sealed class RegisterCommand : Command<RegisterSettings>
{
    public override int Execute(CommandContext context, RegisterSettings settings)
    {
        var env = new TackEnvironment();
        var spec = ToolSpec.Parse(settings.Spec);
        string binDir = Path.GetFullPath(settings.BinDir);
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
        AnsiConsole.MarkupLine($"[green]registered[/] {Markup.Escape(spec.Tool)}@{Markup.Escape(spec.Version!)} -> {Markup.Escape(binDir)}");
        AnsiConsole.MarkupLine($"[grey]exposes:[/] {Markup.Escape(string.Join(", ", exposes))}");
        Mutations.ReportReshim(env.Reshim(config));
        if (!Render.OnPath(env.ShimsDir))
            AnsiConsole.MarkupLine("[yellow]note:[/] the shims dir is not on PATH yet - installing tack wires it up, or run [green]tack doctor[/].");
        return 0;
    }
}

// ---- bind ----------------------------------------------------------------------------------------

public sealed class BindSettings : CommandSettings
{
    [CommandArgument(0, "<glob>")]
    [Description("A directory glob, e.g. C:/work/employer/**")]
    public string Glob { get; init; } = "";

    [CommandArgument(1, "<tool@version>")]
    public string Spec { get; init; } = "";

    [CommandOption("--enforce")]
    [Description("Make this binding beat a repo tack.yml (org enforcement).")]
    public bool Enforce { get; init; }

    public override ValidationResult Validate()
    {
        if (string.IsNullOrWhiteSpace(Glob)) return ValidationResult.Error("A directory glob is required");
        var s = ToolSpec.Parse(Spec);
        if (string.IsNullOrEmpty(s.Tool) || string.IsNullOrEmpty(s.Version))
            return ValidationResult.Error("Specify tool@version, e.g. node@18.19.0");
        return ValidationResult.Success();
    }
}

public sealed class BindCommand : Command<BindSettings>
{
    public override int Execute(CommandContext context, BindSettings settings)
    {
        var env = new TackEnvironment();
        var spec = ToolSpec.Parse(settings.Spec);
        var config = env.Load();

        config.Bindings.Add(new Binding
        {
            Glob = settings.Glob,
            Tools = { [spec.Tool] = spec.Version! },
            Enforce = settings.Enforce,
        });

        env.Save(config);
        AnsiConsole.MarkupLine($"[green]bound[/] {Markup.Escape(settings.Glob)} -> {Markup.Escape(spec.Tool)}@{Markup.Escape(spec.Version!)}"
            + (settings.Enforce ? " [red](enforced)[/]" : ""));
        Mutations.ReportReshim(env.Reshim(config));
        return 0;
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
