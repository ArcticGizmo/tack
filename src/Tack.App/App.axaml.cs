using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Palette.Theming;
using Tack.App.Services;
using Tack.App.ViewModels;

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
            var services = new TackServices();
            var dialogs = new DialogService(window);
            window.DataContext = new MainWindowViewModel(services, dialogs);
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
