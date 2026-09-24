using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Tack.Core.Maintenance;
using Tack.Core.Resolution;

namespace Tack.App.ViewModels;

/// <summary>UI-side rendering of Core enums - the app's equivalent of the CLI's Render helper, returning
/// labels and brushes instead of Spectre markup so the same "why this version" story reads the same way.
/// The row colours come from the active Nord palette (the same token brushes the XAML paints with), so a
/// code-built row matches a XAML-painted one. Fallbacks are the Nord Aurora hues, used only if the palette
/// isn't initialised (design-time / tests).</summary>
internal static class Presentation
{
    // Resolve a palette token brush from Application.Resources, or fall back to a fixed Nord hue.
    private static IBrush Token(string key, string fallbackHex)
    {
        if (Application.Current is { } app &&
            app.TryGetResource(key, ThemeVariant.Dark, out var value) && value is IBrush brush)
            return brush;
        return Brush.Parse(fallbackHex);
    }

    public static IBrush Ok => Token("OkBrush", "#A3BE8C");        // Nord Aurora green
    public static IBrush Warn => Token("WarnBrush", "#EBCB8B");    // Nord Aurora yellow
    public static IBrush Fail => Token("DangerBrush", "#BF616A");  // Nord Aurora red
    public static IBrush Muted => Token("FaintBrush", "#616E88");  // Nord muted slate
    public static IBrush Accent => Token("AccentBrush", "#88C0D0"); // Nord Frost

    public static string Describe(Resolution r) => r.Source switch
    {
        ResolutionSource.Passthrough => "(passthrough to PATH)",
        ResolutionSource.Unregistered => "(unregistered)",
        ResolutionSource.VersionNotInstalled => $"{r.Version} (NOT INSTALLED)",
        _ => r.Version ?? "?",
    };

    public static string SourceLabel(ResolutionSource s) => s switch
    {
        ResolutionSource.EnvOverride => "env override",
        ResolutionSource.EnforcedZone => "enforced zone",
        ResolutionSource.TackYml => "tack.yml",
        ResolutionSource.Zone => "zone",
        ResolutionSource.Default => "default",
        ResolutionSource.Passthrough => "passthrough",
        ResolutionSource.VersionNotInstalled => "missing",
        ResolutionSource.Unregistered => "unregistered",
        _ => "-",
    };

    public static IBrush SourceBrush(ResolutionSource s) => s switch
    {
        ResolutionSource.EnvOverride => Accent,
        ResolutionSource.EnforcedZone => Fail,
        ResolutionSource.TackYml => Ok,
        ResolutionSource.Zone => Accent,
        ResolutionSource.Default => Muted,
        ResolutionSource.Passthrough => Muted,
        ResolutionSource.VersionNotInstalled => Fail,
        ResolutionSource.Unregistered => Muted,
        _ => Muted,
    };

    public static string Glyph(CheckStatus s) => s switch
    {
        CheckStatus.Ok => "OK",
        CheckStatus.Warn => "WARN",
        _ => "FAIL",
    };

    public static IBrush StatusBrush(CheckStatus s) => s switch
    {
        CheckStatus.Ok => Ok,
        CheckStatus.Warn => Warn,
        _ => Fail,
    };
}
