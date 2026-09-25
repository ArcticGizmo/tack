using System.ComponentModel;
using System.Reflection;
using Spectre.Console;
using Spectre.Console.Cli;
using Tack.Core.Changelog;

namespace Tack.Cli.Commands;

// ---- changelog -----------------------------------------------------------------------------------

public sealed class ChangelogSettings : CommandSettings
{
    [CommandOption("-a|--all")]
    [Description("Show every released version, not just the latest.")]
    public bool All { get; init; }

    [CommandOption("-n|--number <COUNT>")]
    [Description("Show the most recent COUNT versions.")]
    public int? Number { get; init; }
}

/// <summary>
/// Prints tack's changelog from the embedded <c>CHANGELOG.md</c>. Latest release by default; <c>--all</c> for
/// the full history, or <c>--number N</c> for the most recent N. Rendered with Spectre so it reads like the
/// rest of the CLI.
/// </summary>
public sealed class ChangelogCommand : Command<ChangelogSettings>
{
    public override int Execute(CommandContext context, ChangelogSettings settings)
    {
        var markdown = LoadEmbedded();
        if (markdown is null)
        {
            AnsiConsole.MarkupLine("[red]changelog is unavailable[/] (not embedded in this build).");
            return 1;
        }

        var versioned = ChangelogParser.Parse(markdown).Where(s => s.Version is not null).ToList();
        if (versioned.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No released versions in the changelog yet.[/]");
            return 0;
        }

        IEnumerable<ChangelogSection> show = settings.All
            ? versioned
            : versioned.Take(settings.Number is { } n && n > 0 ? n : 1);

        bool first = true;
        foreach (var section in show)
        {
            if (!first) AnsiConsole.WriteLine();
            first = false;
            RenderSection(section);
        }
        return 0;
    }

    private static void RenderSection(ChangelogSection section)
    {
        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(section.Display)}[/] [grey]{Markup.Escape(HeadingDate(section.Heading))}[/]");
        foreach (var line in section.Block)
        {
            var text = line.TrimEnd();
            if (text.StartsWith("## ")) continue; // heading already rendered above
            if (text.StartsWith("- ") || text.StartsWith("* "))
                AnsiConsole.MarkupLine($"  [grey]-[/] {Markup.Escape(ChangelogMarkup.StripInline(text[2..]))}");
            else if (text.StartsWith("### "))
                AnsiConsole.MarkupLine($"  [bold]{Markup.Escape(ChangelogMarkup.StripInline(text[4..]))}[/]");
            else if (text.StartsWith("> "))
                AnsiConsole.MarkupLine($"  [italic grey]{Markup.Escape(ChangelogMarkup.StripInline(text[2..]))}[/]");
            else if (text.Length > 0)
                AnsiConsole.MarkupLine($"  {Markup.Escape(ChangelogMarkup.StripInline(text))}");
        }
    }

    /// <summary>Pulls the trailing " - YYYY-MM-DD" off a heading like "[v0.1.0] - 2026-09-23", or "".</summary>
    private static string HeadingDate(string heading)
    {
        int dash = heading.IndexOf(" - ", StringComparison.Ordinal);
        return dash >= 0 ? heading[(dash + 3)..].Trim() : "";
    }

    /// <summary>Reads the changelog embedded at build time (csproj: <c>Tack.CHANGELOG.md</c>), or null.</summary>
    private static string? LoadEmbedded()
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Tack.CHANGELOG.md");
            if (s is null) return null;
            using var reader = new StreamReader(s);
            return reader.ReadToEnd();
        }
        catch { return null; }
    }
}
