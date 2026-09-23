using Avalonia;
using Tack.App.Services;
using Velopack;

namespace Tack.App;

internal static class Program
{
    // Velopack requires its bootstrap as the very first thing in the main exe's entry point. tack-ui is the
    // Velopack mainExe, so it owns the install lifecycle: on install/update it wires the shims dir + install
    // dir onto the user PATH AND regenerates shims from existing config with this build's shim binary; on
    // uninstall it strips the PATH entries. On a normal launch these are no-ops and Run() returns, after
    // which Avalonia starts.
    [STAThread]
    public static int Main(string[] args)
    {
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => InstallHook.Apply())
            .OnAfterUpdateFastCallback(_ => InstallHook.Apply())
            .OnBeforeUninstallFastCallback(_ => InstallHook.Remove())
            .Run();

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Referenced by the Avalonia previewer/tooling by convention.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
