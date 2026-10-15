using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using PRJ2_Extractor.ViewModels;

namespace PRJ2_Extractor.Converters;

/// <summary>true -> Collapsed, false -> Visible (the opposite of BooleanToVisibilityConverter).</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Colour of the marker in front of a log line.</summary>
public sealed class LogLevelBrushConverter : IValueConverter
{
    private static readonly Brush Info = Freeze(new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)));
    private static readonly Brush Success = Freeze(new SolidColorBrush(Color.FromRgb(0x1E, 0x8E, 0x4E)));
    private static readonly Brush Warning = Freeze(new SolidColorBrush(Color.FromRgb(0xB7, 0x79, 0x1F)));
    private static readonly Brush Error = Freeze(new SolidColorBrush(Color.FromRgb(0xD6, 0x45, 0x45)));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        LogLevel.Success => Success,
        LogLevel.Warning => Warning,
        LogLevel.Error => Error,
        _ => Info
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}

/// <summary>Symbol of a log line: a tick, an exclamation mark, a cross or a dot.</summary>
public sealed class LogLevelGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        LogLevel.Success => "\u2713",
        LogLevel.Warning => "!",
        LogLevel.Error => "\u2715",
        _ => "\u2022"
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
