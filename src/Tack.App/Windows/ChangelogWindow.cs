using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Tack.App.Changelog;
using Tack.Core.Changelog;

namespace Tack.App.Windows;

/// <summary>
/// The post-update "what's new" card: a headline, a scrollable list of the changelog sections released since
/// the version that last ran here, and two buttons - Close, and "Don't show changelogs again" which suppresses
/// future pop-ups via <see cref="_onSuppress"/>. Shown once per update from app startup; the entries are picked
/// by <see cref="ChangelogParser.UnseenSince"/>. Styled off the Nord palette so it reads as part of the app.
/// </summary>
internal sealed class ChangelogWindow : Window
{
    private readonly Action _onSuppress;

    public ChangelogWindow(string headline, string subhead, IReadOnlyList<ChangelogSection> sections, Action onSuppress)
    {
        _onSuppress = onSuppress;

        Title = "What's new in tack";
        Width = 520;
        Height = 600;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brush("FormBgBrush");

        Content = BuildCard(headline, subhead, sections);
    }

    private Control BuildCard(string headline, string subhead, IReadOnlyList<ChangelogSection> sections)
    {
        var title = new TextBlock { Text = headline, Foreground = Brush("TitleBrush"), FontWeight = FontWeight.SemiBold, FontSize = 18 };
        var sub = new TextBlock
        {
            Text = subhead, Foreground = Brush("FaintBrush"), FontSize = 12,
            Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap,
        };
        var header = new StackPanel { Children = { title, sub } };

        var body = new StackPanel { Spacing = 2 };
        if (sections.Count == 0)
        {
            body.Children.Add(new TextBlock { Text = "No changelog entries in that range.", Foreground = Brush("FaintBrush") });
        }
        for (int i = 0; i < sections.Count; i++)
        {
            if (i > 0)
                body.Children.Add(new Border { Height = 1, Background = Brush("BorderBrush"), Margin = new Thickness(0, 12, 0, 12) });
            ChangelogMarkdown.Render(body, sections[i].Block);
        }

        var scroller = new ScrollViewer
        {
            Content = body,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 14, 0, 14),
        };

        var suppress = new Button { Content = "Don't show changelogs again", Classes = { "secondary" }, Cursor = new Cursor(StandardCursorType.Hand) };
        suppress.Click += (_, _) => { try { _onSuppress(); } catch { } Close(); };

        var close = new Button { Content = "Close", Classes = { "primary" }, MinWidth = 84, Cursor = new Cursor(StandardCursorType.Hand) };
        close.Click += (_, _) => Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { suppress, close },
        };

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(24) };
        Grid.SetRow(header, 0);
        Grid.SetRow(scroller, 1);
        Grid.SetRow(buttons, 2);
        grid.Children.Add(header);
        grid.Children.Add(scroller);
        grid.Children.Add(buttons);
        return grid;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        base.OnKeyDown(e);
    }

    private static IBrush Brush(string key) =>
        Application.Current!.TryGetResource(key, Application.Current.ActualThemeVariant, out var b) && b is IBrush br
            ? br
            : Brushes.Gray;
}
