using System.Reflection;
using Spectre.Console.Cli;
using Tack.Cli.Commands;
using Velopack;

// tack CLI.
//
// Velopack bootstrap runs first in every shipped exe EXCEPT the tiny hot-path shim. On a normal run it's a
// no-op; when Velopack invokes tack with its own hook args it handles them and exits. Then a Spectre.Console
// command app dispatches. Every command is a thin shell over Tack.Core (Core decides; the CLI formats).
VelopackApp.Build().Run();

var app = new CommandApp();
app.Configure(cfg =>
{
    cfg.SetApplicationName("tack");
    cfg.SetApplicationVersion(ResolveVersion());

    cfg.AddCommand<InfoCommand>("info")
        .WithDescription("Show the resolved version and source for a tool in this directory.");
    cfg.AddCommand<WhichCommand>("which")
        .WithDescription("Print the absolute path a tool resolves to (scriptable).");
    cfg.AddCommand<ListCommand>("list").WithAlias("ls")
        .WithDescription("List registered tools and versions.");
    cfg.AddCommand<ShimsCommand>("shims")
        .WithDescription("List generated shims and PATH health.");
    cfg.AddCommand<DoctorCommand>("doctor")
        .WithDescription("Diagnose PATH and shim health.");
    cfg.AddCommand<RegisterCommand>("register")
        .WithDescription("Register an existing tool install in the central registry.");
    cfg.AddCommand<BindCommand>("bind")
        .WithDescription("Add a central directory binding (managed without a repo tack.yml).");
    cfg.AddCommand<UseCommand>("use")
        .WithDescription("Pin a tool version in this directory (writes tack.yml).");
    cfg.AddCommand<ReshimCommand>("reshim")
        .WithDescription("Recompile central config and regenerate the shims.");
    cfg.AddCommand<OpenCommand>("open").WithAlias("ui")
        .WithDescription("Launch the tack desktop UI.");
});

return app.Run(args);

static string ResolveVersion()
{
    var asm = Assembly.GetExecutingAssembly();
    string v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? asm.GetName().Version?.ToString()
        ?? "0.0.0";
    int plus = v.IndexOf('+');
    return plus >= 0 ? v[..plus] : v;
}
