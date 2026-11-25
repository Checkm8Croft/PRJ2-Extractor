using System.Globalization;
using Avalonia.Data.Converters;
using PRJ2_Extractor.ViewModels;

namespace PRJ2_Extractor.Converters;

/// <summary>Symbol of a log line: a tick, an exclamation mark, a cross or a dot.</summary>
public sealed class LogLevelGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        LogLevel.Success => "\u2713",
        LogLevel.Warning => "!",
        LogLevel.Error => "\u2715",
        _ => "\u2022"
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
