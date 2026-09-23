using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Tack.App.Services;
using Tack.App.ViewModels;

namespace Tack.App;

public class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
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
