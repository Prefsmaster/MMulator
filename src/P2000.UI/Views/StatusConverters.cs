using Avalonia.Data.Converters;
using Avalonia.Media;
using System.Globalization;

namespace P2000.UI.Views;

/// <summary>Colors the state label green when running, yellow when paused.</summary>
public sealed class StateTextToColorConverter : IValueConverter
{
    public static readonly StateTextToColorConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is "Paused" ? Brushes.Yellow : Brushes.LightGreen;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Lights a cassette activity LED: orange when active, dark grey when idle.</summary>
public sealed class BoolToLedBrushConverter : IValueConverter
{
    public static readonly BoolToLedBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Brushes.Orange : Brush.Parse("#444");

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Returns true when the bound value equals the converter parameter (enum radio-button
/// IsChecked binding).</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public static readonly EnumEqualsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.Equals(parameter) == true;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Colors a memory byte yellow when changed, grey when unchanged.</summary>
public sealed class BoolToChangedBrushConverter : IValueConverter
{
    public static readonly BoolToChangedBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Brush.Parse("#ffd700") : Brush.Parse("#999");

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Returns true when an int is zero (for empty-state visibility). Pass
/// <c>ConverterParameter=invert</c> to flip (visible when non-zero).</summary>
public sealed class ZeroToBoolConverter : IValueConverter
{
    public static readonly ZeroToBoolConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isZero = value is int i && i == 0;
        return parameter is "invert" ? !isZero : isZero;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
