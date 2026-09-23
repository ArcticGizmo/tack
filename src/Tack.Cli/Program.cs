using System.Diagnostics;
using System.Reflection;
using Velopack;

// tack CLI (M0 skeleton).
//
// Velopack bootstrap runs first in every shipped exe EXCEPT the tiny hot-path shim (which must stay minimal
// and fast). On a normal run this is a no-op and returns immediately; when Velopack invokes tack with its
// own hook args, it handles them and exits.
VelopackApp.Build().Run();

string version = ResolveVersion();
string cmd = args.Length > 0 ? args[0] : "";

switch (cmd)
{
    case "--version":
    case "-v":
    case "version":
        Console.WriteLine($"tack {version}");
        return 0;

    case "open":
    case "ui":
        return LaunchUi();

    case "":
        Console.WriteLine($"tack {version}");
        Console.WriteLine("usage: tack <command>");
        Console.WriteLine("  M0 skeleton knows: --version, open");
        Console.WriteLine("  the full CLI (info/which/list/use/register/bind/reshim/doctor/shims) is M3");
        return 0;

    default:
        Console.Error.WriteLine($"tack: unknown command '{cmd}' (M0 skeleton knows only --version, open)");
        return 2;
}

static string ResolveVersion()
{
    var asm = Assembly.GetExecutingAssembly();
    string v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? asm.GetName().Version?.ToString()
        ?? "0.0.0";
    int plus = v.IndexOf('+'); // strip any +<metadata> defensively
    return plus >= 0 ? v[..plus] : v;
}

// `tack open` launches tack-ui.exe (the Avalonia head) from the same install dir. Console app -> UI, so
// UseShellExecute detaches it without tying it to this process's console.
static int LaunchUi()
{
    string ui = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "tack-ui.exe" : "tack-ui");
    if (!File.Exists(ui))
    {
        Console.Error.WriteLine($"tack: tack-ui not found next to tack ({ui}).");
        return 1;
    }
    try
    {
        Process.Start(new ProcessStartInfo(ui) { UseShellExecute = true });
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"tack: could not launch the UI: {ex.Message}");
        return 1;
    }
}
