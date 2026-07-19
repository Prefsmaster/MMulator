using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
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

/// <summary>Highlights the PC row yellow in the disassembly listing.</summary>
public sealed class BoolToPcBrushConverter : IValueConverter
{
    public static readonly BoolToPcBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Brush.Parse("#2a2400") : Brushes.Transparent;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Colors the breakpoint gutter dot red when set, transparent when not.</summary>
public sealed class BoolToBpDotConverter : IValueConverter
{
    public static readonly BoolToBpDotConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Brush.Parse("#c0392b") : Brushes.Transparent;

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

/// <summary>Cassette deck write-protect toggle: closed padlock when protected, open padlock
/// when writable — a single glyph carries the whole signal, no separate checkmark.</summary>
public sealed class BoolToPadlockIconConverter : IValueConverter
{
    public static readonly BoolToPadlockIconConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "🔒" : "🔓";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Label text paired with <see cref="BoolToPadlockIconConverter"/>.</summary>
public sealed class BoolToWriteProtectLabelConverter : IValueConverter
{
    public static readonly BoolToWriteProtectLabelConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "Write protected" : "Write enabled";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Highlights a soft-keyboard key while a sticky Shift/CODE/Lock is engaged.</summary>
public sealed class BoolToKeyBrushConverter : IValueConverter
{
    public static readonly BoolToKeyBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Brush.Parse("#4a7a4a") : Brush.Parse("#3a3a3a");

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}


/// <summary>Soft-keyboard mode-toggle button label.</summary>
public sealed class ModeLabelConverter : IValueConverter
{
    public static readonly ModeLabelConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "Standard-Host" : "P2000-Authentic";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Loads a soft-keyboard key icon from an <c>avares://</c> URI string
/// (<see cref="P2000.UI.ViewModels.SoftKeyVm.BaseIconUri"/>/<c>ShiftedIconUri</c>).</summary>
public sealed class IconUriToBitmapConverter : IValueConverter
{
    public static readonly IconUriToBitmapConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string uri) return null;
        using var stream = AssetLoader.Open(new Uri(uri));
        return new Bitmap(stream);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
