using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace RepoNLauncher.Converters;

/// <summary>Visible when the string is non-empty. Pass ConverterParameter="Invert" to flip (e.g. for a placeholder).</summary>
public class StringNotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasText = !string.IsNullOrEmpty(value as string);
        var invert = string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase);
        return hasText != invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
