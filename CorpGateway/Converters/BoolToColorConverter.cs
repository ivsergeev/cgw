using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace CorpGateway.Converters;

/// <summary>
/// Converts a bool to a color. ConverterParameter format: "TrueColor:FalseColor"
/// e.g. "#22C55E:#EF4444"
/// </summary>
public class BoolToColorConverter : IValueConverter
{
    public static readonly BoolToColorConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && parameter is string s)
        {
            var parts = s.Split(':');
            if (parts.Length == 2)
                return Color.Parse(b ? parts[0] : parts[1]);
        }
        return Colors.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
