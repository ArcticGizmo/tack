using Avalonia.Media;
using Tack.Core.Maintenance;
using Tack.Core.Resolution;

namespace Tack.App.ViewModels;

/// <summary>One tool's resolution in a chosen directory - the row behind the dashboard and inspector tables.</summary>
public sealed class ResolutionRow
{
    public required string Tool { get; init; }
    public required string Version { get; init; }
    public required string SourceLabel { get; init; }
    public required IBrush SourceBrush { get; init; }
    public required string Why { get; init; }
    public required string Binary { get; init; }
    public bool Resolved { get; init; }

    public static ResolutionRow From(Resolution r)
    {
        string binary = "-";
        if (r.Resolved && r.BinDir is not null)
            binary = Services.TackServices.LocateBinary(r.BinDir, r.Tool ?? r.ExposedName)
                     ?? $"(no '{r.Tool ?? r.ExposedName}' in {r.BinDir})";

        return new ResolutionRow
        {
            Tool = r.Tool ?? r.ExposedName,
            Version = Presentation.Describe(r),
            SourceLabel = Presentation.SourceLabel(r.Source),
            SourceBrush = Presentation.SourceBrush(r.Source),
            Why = r.Detail ?? "",
            Binary = binary,
            Resolved = r.Resolved,
        };
    }
}

/// <summary>One installed version of a registered tool, for the registry editor.</summary>
public sealed class ToolVersionRow
{
    public required string Tool { get; init; }
    public required string Version { get; init; }
    public required string BinDir { get; init; }
    public required string Exposes { get; init; }
    public bool IsDefault { get; init; }
    public bool BinDirExists { get; init; }
    public string DefaultMark => IsDefault ? "default" : "";
    public IBrush BinDirBrush => BinDirExists ? Presentation.Muted : Presentation.Fail;
}

/// <summary>One central zone, for the zones editor.</summary>
public sealed class ZoneRow
{
    public required string Path { get; init; }
    public required string Tool { get; init; }
    public required string Version { get; init; }
    public bool Enforce { get; init; }
    public string EnforceMark => Enforce ? "enforced" : "";
}

/// <summary>One PATH-doctor check.</summary>
public sealed class DoctorRow
{
    public required string Glyph { get; init; }
    public required IBrush StatusBrush { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }

    public static DoctorRow From(DoctorCheck c) => new()
    {
        Glyph = Presentation.Glyph(c.Status),
        StatusBrush = Presentation.StatusBrush(c.Status),
        Title = c.Title,
        Detail = c.Detail,
    };
}

/// <summary>One entry of the effective PATH, with the shims dir highlighted.</summary>
public sealed class PathEntryRow
{
    public required int Order { get; init; }
    public required string Path { get; init; }
    public bool IsShims { get; init; }
    public string Note { get; init; } = "";
    public IBrush Brush => IsShims ? Presentation.Ok : Presentation.Muted;
    public FontWeight Weight => IsShims ? FontWeight.SemiBold : FontWeight.Normal;
}
