using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace DynamicIsland.Windows.Infrastructure;

/// <summary>
/// Multi-value converter for a progress ring's StrokeDashArray. Inputs: [0] progress (0–100),
/// [1] the outline's total length in stroke-thickness units (perimeter / StrokeThickness). Produces a
/// single dash that sweeps proportionally around the (squircle) outline.
/// </summary>
public sealed class RingProgressConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var progress = Math.Clamp(System.Convert.ToDouble(values[0], culture) / 100.0, 0d, 1d);
        var units = System.Convert.ToDouble(values[1], culture);
        if (units <= 0) units = 1;
        var on = Math.Max(0.0001, progress * units);
        var off = Math.Max(0.0001, units - on);
        return new DoubleCollection { on, off };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Visible when the bound value (string) equals the ConverterParameter; else Collapsed.</summary>
public sealed class StringEqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>True when the bound value (string) equals the ConverterParameter (for selection highlighting).</summary>
public sealed class StringEqualsToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Wraps a double into a uniform <see cref="CornerRadius"/> (for binding a single radius value).</summary>
public sealed class DoubleToCornerRadiusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => new CornerRadius(System.Convert.ToDouble(value, culture));

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
