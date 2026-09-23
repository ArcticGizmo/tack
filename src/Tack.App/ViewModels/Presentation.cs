using Avalonia.Media;
using Tack.Core.Maintenance;
using Tack.Core.Resolution;

namespace Tack.App.ViewModels;

/// <summary>UI-side rendering of Core enums - the app's equivalent of the CLI's Render helper, returning
/// labels and brushes instead of Spectre markup so the same "why this version" story reads the same way.</summary>
internal static class Presentation
{
    public static readonly IBrush Ok = Brush.Parse("#4CAF50");
    public static readonly IBrush Warn = Brush.Parse("#E0A800");
    public static readonly IBrush Fail = Brush.Parse("#E53935");
    public static readonly IBrush Muted = Brush.Parse("#9E9E9E");
    public static readonly IBrush Accent = Brush.Parse("#42A5F5");

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
        ResolutionSource.EnforceBinding => "enforced binding",
        ResolutionSource.TackYml => "tack.yml",
        ResolutionSource.Binding => "binding",
        ResolutionSource.Default => "default",
        ResolutionSource.Passthrough => "passthrough",
        ResolutionSource.VersionNotInstalled => "missing",
        ResolutionSource.Unregistered => "unregistered",
        _ => "-",
    };

    public static IBrush SourceBrush(ResolutionSource s) => s switch
    {
        ResolutionSource.EnvOverride => Accent,
        ResolutionSource.EnforceBinding => Fail,
        ResolutionSource.TackYml => Ok,
        ResolutionSource.Binding => Accent,
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
