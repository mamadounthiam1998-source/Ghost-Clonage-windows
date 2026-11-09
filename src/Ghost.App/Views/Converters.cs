using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Ghost.App.Views;

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>ok → vert, error → rouge, cancelled → ambre.</summary>
public sealed class ResultKindToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string key = (value as string) switch { "ok" => "OkBrush", "error" => "DangerBrush", _ => "WarnBrush" };
        return Application.Current.Resources[key] as Brush ?? Brushes.White;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class ResultKindToGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value as string) switch { "ok" => "", "error" => "", _ => "" };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
