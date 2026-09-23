namespace Tack.Core;

/// <summary>
/// The well-known per-user paths tack uses. Mirrors the on-disk layout in the scope plan (section 3.2):
/// everything lives under %LOCALAPPDATA%\tack (or %LOCALAPPDATA%\tack (Dev) for a dev build - see
/// <see cref="TackProfile"/>). The Velopack install dir (current\) is separate and found via
/// AppContext.BaseDirectory at runtime.
/// </summary>
public static class TackPaths
{
    /// <summary>%LOCALAPPDATA%\tack (or \tack (Dev) under a dev profile).</summary>
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), TackProfile.DataFolderName);

    /// <summary>The directory of shim exes that goes on PATH (one *.exe per exposed tool binary).</summary>
    public static string ShimsDir => Path.Combine(Root, "shims");

    /// <summary>Central, tool-managed config: registry + bindings + defaults (JSON).</summary>
    public static string ConfigJson => Path.Combine(Root, "config.json");

    /// <summary>Compiled fast-lookup the shim reads (regenerated on config change).</summary>
    public static string ResolvedJson => Path.Combine(Root, "resolved.json");

    /// <summary>Small UI preferences (e.g. the changelog "last seen" bookkeeping); never read by the shim.</summary>
    public static string UiStateJson => Path.Combine(Root, "ui-state.json");
}
