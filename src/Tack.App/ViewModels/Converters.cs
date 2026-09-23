using System.Globalization;
using Avalonia.Data.Converters;

namespace Tack.App.ViewModels;

/// <summary>True when the bound int equals the ConverterParameter - drives the nav rail's active pill.</summary>
public sealed class IntEqualsConverter : IValueConverter
{
    public static readonly IntEqualsConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int a = value is int i ? i : int.TryParse(value?.ToString(), out var vi) ? vi : -1;
        int b = parameter is int j ? j : int.TryParse(parameter?.ToString(), out var pi) ? pi : -2;
        return a == b;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
