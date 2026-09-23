using Avalonia;
using Tack.Core.Platform;
using Velopack;

namespace Tack.App;

internal static class Program
{
    // Velopack requires its bootstrap as the very first thing in the main exe's entry point. tack-ui is the
    // Velopack mainExe, so it owns the install lifecycle: on install/update it wires the shims dir + install
    // dir onto the user PATH; on uninstall it strips them. On a normal launch these are no-ops and Run()
    // returns, after which Avalonia starts.
    [STAThread]
    public static int Main(string[] args)
    {
        var pathInstaller = new WindowsPathInstaller();

        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => pathInstaller.Register())
            .OnAfterUpdateFastCallback(_ => pathInstaller.Register())
            .OnBeforeUninstallFastCallback(_ => pathInstaller.Unregister())
            .Run();

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Referenced by the Avalonia previewer/tooling by convention.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
