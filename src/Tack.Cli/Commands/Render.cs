using Tack.Core.Resolution;

namespace Tack.Cli.Commands;

/// <summary>Small shared rendering + PATH helpers for the commands.</summary>
internal static class Render
{
    public static string Describe(Resolution r) => r.Source switch
    {
        ResolutionSource.Passthrough => "(passthrough to PATH)",
        ResolutionSource.ZoneNone => "(none - passthrough to PATH)",
        ResolutionSource.Unregistered => "(unregistered)",
        ResolutionSource.VersionNotInstalled => $"{r.Version} (NOT INSTALLED)",
        _ => r.Version ?? "?",
    };

    public static string SourceMarkup(ResolutionSource s) => s switch
    {
        ResolutionSource.EnvOverride => "[aqua]env[/]",
        ResolutionSource.EnforcedZone => "[red]enforced zone[/]",
        ResolutionSource.TackYml => "[green]tack.yml[/]",
        ResolutionSource.Zone => "[blue]zone[/]",
        ResolutionSource.Default => "[grey]default[/]",
        ResolutionSource.ZoneNone => "[yellow]none zone[/]",
        ResolutionSource.Passthrough => "[grey]passthrough[/]",
        ResolutionSource.VersionNotInstalled => "[red]missing[/]",
        _ => "[grey]-[/]",
    };

    public static bool OnPath(string dir)
    {
        string target = Norm(dir);
        foreach (var t in new[] { EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            var pv = Environment.GetEnvironmentVariable("PATH", t);
            if (pv is null) continue;
            if (pv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .Any(p => Norm(p) == target))
                return true;
        }
        return false;
    }

    private static string Norm(string p)
    {
        try { return Path.GetFullPath(p).TrimEnd('\\', '/').ToLowerInvariant(); }
        catch { return p.TrimEnd('\\', '/').ToLowerInvariant(); }
    }
}
