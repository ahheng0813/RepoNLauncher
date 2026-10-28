using System.Globalization;
using System.Windows.Data;

namespace RepoNLauncher.Converters;

/// <summary>True when the bound string starts with ConverterParameter. Used to color console log lines by origin.</summary>
public class StringStartsWithConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && parameter is string prefix && s.StartsWith(prefix, StringComparison.Ordinal);

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
