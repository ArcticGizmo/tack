using System.Reflection;
using Spectre.Console.Cli;
using Tack.Cli;
using Tack.Cli.Commands;
using Velopack;

// tack CLI.
//
// Velopack requires its bootstrap as the very first thing in the main exe's entry point. tack.exe is the
// Velopack mainExe, so it owns the install lifecycle: on install/update it puts the shims dir + install dir on
// the USER PATH (no admin; tack never touches the system PATH) and regenerates shims from existing config with
// this build's shim binary; on uninstall it strips those entries. On a normal run these are no-ops and Run()
// returns. Then a Spectre.Console command app dispatches. Every command is a thin shell over Tack.Core (Core
// decides; the CLI formats).
var velopack = VelopackApp.Build();
if (OperatingSystem.IsWindows()) velopack = InstallHook.Wire(velopack);
velopack.Run();

var app = new CommandApp();
app.Configure(cfg =>
{
    cfg.SetApplicationName("tack");
    cfg.SetApplicationVersion(ResolveVersion());

    cfg.AddCommand<InfoCommand>("info")
        .WithDescription("Show the resolved version and source for a tool in this directory.");
    cfg.AddCommand<WhichCommand>("which")
        .WithDescription("Print the absolute path a tool resolves to (scriptable).");
    // Command groups are singular (`tack tool add`); the plural is a silent alias for muscle memory.
    cfg.AddBranch<CommandSettings>("tool", tool =>
    {
        tool.SetDescription("Manage the central tool registry (add, remove, list).");
        tool.AddCommand<ToolsAddCommand>("add")
            .WithDescription("Register an existing tool install in the central registry.");
        tool.AddCommand<ToolsInstallCommand>("install")
            .WithDescription("Download Node or Python from its publisher and register it (e.g. node@lts, python@3.12).");
        tool.AddCommand<ToolsAvailableCommand>("available")
            .WithDescription("List the Node or Python versions tool install can install here.");
        tool.AddCommand<ToolsRemoveCommand>("remove")
            .WithDescription("Remove tool versions (interactive picker when no version is given).");
        tool.AddCommand<ToolsListCommand>("list")
            .WithDescription("List registered tools and versions (--expand for copyable paths).");
    }).WithAlias("tools");
    cfg.AddCommand<SetupCommand>("setup")
        .WithDescription("Put tack on your user PATH and stamp your shims. The installer does this; re-run it to repair (--remove to undo).");
    cfg.AddCommand<DoctorCommand>("doctor")
        .WithDescription("Diagnose PATH and shim health (--fix to repair).");
    cfg.AddCommand<DisableCommand>("disable")
        .WithDescription("Turn tack off for you: your tool calls fall through to the rest of PATH.");
    cfg.AddCommand<EnableCommand>("enable")
        .WithDescription("Turn tack back on after 'tack disable'.");
    cfg.AddBranch<CommandSettings>("zone", zone =>
    {
        zone.SetDescription("Manage zones: directories (and everything under them) that use a tool version without a repo tack.yml.");
        zone.AddCommand<ZonesAddCommand>("add")
            .WithDescription("Add or update a zone: this directory and everything under it use tool@version.");
        zone.AddCommand<ZonesRemoveCommand>("remove")
            .WithDescription("Remove zones (interactive picker when no directory is given).");
        zone.AddCommand<ZonesListCommand>("list")
            .WithDescription("List zones.");
    }).WithAlias("zones");
    cfg.AddBranch<CommandSettings>("log", log =>
    {
        log.SetDescription("Log every shim call with the process chain that made it, to find what's invoking a tool (on, off, open).");
        log.AddCommand<LogOnCommand>("on")
            .WithDescription("Start logging shim calls.");
        log.AddCommand<LogOffCommand>("off")
            .WithDescription("Stop logging shim calls (the log is kept).");
        log.AddCommand<LogOpenCommand>("open")
            .WithDescription("Open the folder holding the log, with the log selected.");
    });
    cfg.AddCommand<UseCommand>("use")
        .WithDescription("Pin a tool version in this directory (writes tack.yml).");
    cfg.AddCommand<UpdateCommand>("update")
        .WithDescription("Update tack to the latest release (--check to only look).");
    cfg.AddCommand<ChangelogCommand>("changelog")
        .WithDescription("Show what changed in tack (latest release; --all for the full history).");

    // Every mutating command already reshims. This one is for commands installed into a managed version since
    // (npm i -g, pip install), and after a hand-edit of config.json.
    cfg.AddCommand<ReshimCommand>("reshim")
        .WithDescription("Shim commands added to installed versions since (npm i -g, pip install), and regenerate the shims.");
});

return app.Run(args);

static string ResolveVersion() =>
    Tack.Core.Changelog.VersionInfo.Of(Assembly.GetExecutingAssembly());
