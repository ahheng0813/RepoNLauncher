using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace RepoNLauncher.Converters;

/// <summary>Visible when the value is non-null. Pass ConverterParameter="Invert" to flip.</summary>
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isPresent = value != null;
        var invert = string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase);
        var visible = invert ? !isPresent : isPresent;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
