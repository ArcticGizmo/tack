using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Palette.Theming;
using Tack.App.Changelog;
using Tack.App.Services;
using Tack.App.ViewModels;
using Tack.App.Windows;
using Tack.Core.Changelog;

namespace Tack.App;

public class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Nord (Dark) - the curated WCAG-AA palette perch themes from. ThemeManager publishes the palette's
        // semantic token brushes into Application.Resources (PanelBgBrush, FgBrush, AccentBrush, Ok/Warn/
        // DangerBrush, ...), which the views consume via {DynamicResource ...}. Done before the window is
        // built so the first frame is already Nord. Selected from the built-ins with a fallback, so a renamed
        // id in a future package version degrades to the default rather than throwing at startup.
        var nord = PaletteCatalog.All.FirstOrDefault(p => p.Id == "nord-dark") ?? PaletteCatalog.Default;
        ThemeManager.Initialize(this, nord);

        // Point Fluent's own accent (tab selection, focus rings, accent buttons) at the palette's Nord frost
        // accent - otherwise those built-in bits fall back to the Windows system accent, the one non-Nord
        // splash in an otherwise Nord window.
        var accent = Color.Parse("#88C0D0"); // Nord Frost fallback
        if (this.TryGetResource("AccentBrush", ThemeVariant.Dark, out var ab) && ab is ISolidColorBrush sb)
            accent = sb.Color;
        foreach (var key in new[]
        {
            "SystemAccentColor",
            "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3",
            "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3",
        })
            Resources[key] = accent;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Composition root: build the window, wire Core + the folder-picker (which needs the window's
            // StorageProvider) into the shell view model.
            var window = new MainWindow();
            // A dev build runs against an isolated data space (see TackProfile); label the window so it's
            // obvious this isn't touching an installed tack's config/shims.
            window.Title = "tack" + Tack.Core.TackProfile.DisplaySuffix;
            var services = new TackServices();
            var dialogs = new DialogService(window);
            window.DataContext = new MainWindowViewModel(services, dialogs);
            desktop.MainWindow = window;

            // First launch after an update: capture the changelog entries newer than the version that last
            // ran here, then stamp the current version so they're only ever shown once. A null last-seen is a
            // fresh install (or the first run of this feature) - seed it silently, nothing to pop.
            var pending = ResolvePendingChangelog();
            StampSeenVersion();
            if (pending is { Count: > 0 })
                Dispatcher.UIThread.Post(() => ShowChangelog(window, pending), DispatcherPriority.Background);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static readonly UiStateStore _uiStore = new();

    // Picks the changelog sections to surface on this launch: nothing unless the feature is on, we have a
    // prior version on record, and it differs from the current one - then only the sections in between.
    private static IReadOnlyList<ChangelogSection>? ResolvePendingChangelog()
    {
        var state = _uiStore.Load();
        if (!state.ShowChangelogOnUpdate) return null;
        if (string.IsNullOrWhiteSpace(state.LastSeenVersion)) return null; // fresh install - nothing to show
        string current = VersionInfo.Of(typeof(App).Assembly);
        if (state.LastSeenVersion == current) return null;                 // same version - no update
        var markdown = ChangelogMarkdown.LoadEmbedded();
        if (markdown is null) return null;
        var sections = ChangelogParser.UnseenSince(markdown, state.LastSeenVersion, current);
        return sections.Count > 0 ? sections : null;
    }

    // Record the running version as "seen" so the popup fires at most once per update.
    private static void StampSeenVersion()
    {
        var state = _uiStore.Load();
        string current = VersionInfo.Of(typeof(App).Assembly);
        if (state.LastSeenVersion != current)
        {
            state.LastSeenVersion = current;
            _uiStore.Save(state);
        }
    }

    private static void ShowChangelog(Avalonia.Controls.Window owner, IReadOnlyList<ChangelogSection> sections)
    {
        string subhead = sections.Count == 1
            ? $"Updated to {sections[0].Display}."
            : $"Updated to {sections[0].Display} - {sections.Count} releases since {sections[^1].Display}.";
        var win = new ChangelogWindow("What's new in tack", subhead, sections, onSuppress: () =>
        {
            var state = _uiStore.Load();
            state.ShowChangelogOnUpdate = false;
            _uiStore.Save(state);
        });
        win.Show(owner);
    }
}
