using Avalonia.Data.Converters;
using P2000.Machine;
using System.Globalization;

namespace P2000.UI.ViewModels;

/// <summary>Maps a <see cref="RamVariant"/> value to a human-readable description string.</summary>
public sealed class RamVariantDescConverter : IValueConverter
{
    public static readonly RamVariantDescConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            RamVariant.T38  => "T/38  — 16 KB (base only)",
            RamVariant.T54  => "T/54  — 32 KB (+ 16 KB expansion)",
            RamVariant.T102 => "T/102 — 80 KB (+ 16 KB exp. + 48 KB banked)",
            _ => value?.ToString()
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Returns true when a string is non-null and non-empty (for status-message visibility).</summary>
public sealed class NonEmptyStringToBoolConverter : IValueConverter
{
    public static readonly NonEmptyStringToBoolConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => !string.IsNullOrEmpty(value as string);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
