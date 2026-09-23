using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Tack.Core.Changelog;

namespace Tack.App.Changelog;

/// <summary>
/// Loads the embedded <c>CHANGELOG.md</c> and renders its (lightweight) markdown into a stacked column of
/// themed controls, using the app's shared style classes so it matches every other screen. Shared by the
/// What's New tab and the post-update <see cref="Windows.ChangelogWindow"/> so the two read identically.
/// Handles just the subset the changelog uses: <c>## </c>/<c>### </c> headings, <c>-</c>/<c>*</c> bullets,
/// <c>&gt; </c> quotes, <c>---</c> rules, and inline emphasis/links. Ported from perch's ChangelogMarkdown.
/// </summary>
internal static class ChangelogMarkdown
{
    /// <summary>Reads the changelog embedded at build time (csproj: <c>Tack.CHANGELOG.md</c>), or null.</summary>
    public static string? LoadEmbedded()
    {
        try
        {
            using var s = typeof(ChangelogMarkdown).Assembly.GetManifestResourceStream("Tack.CHANGELOG.md");
            if (s is null) return null;
            using var reader = new StreamReader(s);
            return reader.ReadToEnd();
        }
        catch { return null; }
    }

    /// <summary>Appends one control per markdown line into <paramref name="page"/>.</summary>
    public static void Render(StackPanel page, IEnumerable<string> lines)
    {
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("## "))
                page.Children.Add(VersionHeading(ChangelogMarkup.StripInline(line[3..])));
            else if (line.StartsWith("### "))
                page.Children.Add(new TextBlock
                {
                    Text = ChangelogMarkup.StripInline(line[4..]), FontSize = 13, FontWeight = FontWeight.Bold,
                    Margin = new Thickness(0, 6, 0, 2),
                    Foreground = Brush("FgBrush"),
                });
            else if (line.StartsWith("# ")) { /* the H1 title is redundant here */ }
            else if (line.StartsWith("- ") || line.StartsWith("* "))
                page.Children.Add(Bullet(ChangelogMarkup.StripInline(line[2..])));
            else if (line.Trim() == "---")
                page.Children.Add(new Border
                {
                    Height = 1, Background = Brush("BorderBrush"),
                    Margin = new Thickness(0, 10, 0, 10),
                });
            else if (line.StartsWith("> "))
                page.Children.Add(new TextBlock
                {
                    Text = ChangelogMarkup.StripInline(line[2..]), TextWrapping = TextWrapping.Wrap, FontSize = 13,
                    FontStyle = FontStyle.Italic, Foreground = Brush("FaintBrush"), Margin = new Thickness(2, 0, 0, 6),
                });
            else if (line.Trim().Length > 0)
                page.Children.Add(new TextBlock
                {
                    Text = ChangelogMarkup.StripInline(line), TextWrapping = TextWrapping.Wrap, FontSize = 13,
                    Foreground = Brush("FgBrush"), Margin = new Thickness(0, 2, 0, 2),
                });
        }
    }

    private static TextBlock VersionHeading(string text)
    {
        // Split "v0.1.0 - 2026-09-23" into a bold version + a faint date.
        var tb = new TextBlock { Margin = new Thickness(0, 4, 0, 6), FontSize = 15 };
        int dash = text.IndexOf(" - ", StringComparison.Ordinal);
        string version = dash >= 0 ? text[..dash] : text;
        string date = dash >= 0 ? text[(dash + 3)..] : "";
        tb.Inlines = new InlineCollection
        {
            new Run(version) { FontWeight = FontWeight.SemiBold, Foreground = Brush("TitleBrush") },
        };
        if (date.Length > 0)
            tb.Inlines.Add(new Run("   " + date) { FontSize = 12, Foreground = Brush("FaintBrush") });
        return tb;
    }

    private static Control Bullet(string text)
    {
        var dot = new TextBlock { Text = "•", Foreground = Brush("AccentBrush"), Margin = new Thickness(0, 0, 8, 0) };
        DockPanel.SetDock(dot, Dock.Left);
        var body = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = Brush("FgBrush") };
        return new DockPanel { Margin = new Thickness(0, 2, 0, 2), Children = { dot, body } };
    }

    private static IBrush Brush(string key) =>
        Application.Current!.TryGetResource(key, Application.Current.ActualThemeVariant, out var b) && b is IBrush br
            ? br
            : Brushes.Gray;
}
