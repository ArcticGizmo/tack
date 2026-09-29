using Spectre.Console.Cli;
using Tack.Core.Maintenance;

namespace Tack.Cli.Commands;

// `tack elevated <op>`: the hidden steps a UAC relaunch runs, one admin-only write each. They're reachable by
// anything that can start tack, so each works out its own paths from this install (never from arguments) and takes
// nothing but command names, which ShimStamper checks. The parent reads the result back from disk.

public sealed class ElevatedNamesSettings : CommandSettings
{
    [CommandArgument(0, "[names]")]
    public string[] Names { get; init; } = Array.Empty<string>();
}

/// <summary>Stamp shims for the given names (and refresh the shim support files) into this install's shims dir,
/// from this install's own shim binary.</summary>
public sealed class ElevatedShimsCommand : Command<ElevatedNamesSettings>
{
    public override int Execute(CommandContext context, ElevatedNamesSettings settings)
    {
        if (ElevatedGuard.Refuse("shims")) return 1;
        var env = new TackEnvironment();
        var result = ShimStamper.Stamp(settings.Names, env.ShimsDir, env.ShimPayload());
        foreach (var name in result.Rejected)
            Console.Error.WriteLine($"refused: '{name}' is not a valid command name");
        return result.PayloadMissing || result.Rejected.Count > 0 || result.Locked.Count > 0 ? 1 : 0;
    }
}

/// <summary>Remove the shims for exactly the given names from this install's shims dir.</summary>
public sealed class ElevatedPruneCommand : Command<ElevatedNamesSettings>
{
    public override int Execute(CommandContext context, ElevatedNamesSettings settings)
    {
        if (ElevatedGuard.Refuse("prune")) return 1;
        var result = ShimStamper.Prune(settings.Names, new TackEnvironment().ShimsDir);
        foreach (var name in result.Rejected)
            Console.Error.WriteLine($"refused: '{name}' is not a valid command name");
        return result.Rejected.Count > 0 || result.Locked.Count > 0 ? 1 : 0;
    }
}

internal static class ElevatedGuard
{
    /// <summary>True (after saying why) unless this process is elevated on Windows.</summary>
    public static bool Refuse(string op)
    {
        if (OperatingSystem.IsWindows() && Elevation.IsAdministrator()) return false;
        Console.Error.WriteLine($"tack elevated {op}: run through UAC by tack itself; refusing to run unelevated.");
        return true;
    }
}
