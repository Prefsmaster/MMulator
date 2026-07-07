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
