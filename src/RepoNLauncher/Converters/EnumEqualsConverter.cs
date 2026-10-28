using System.Globalization;
using System.Windows.Data;

namespace RepoNLauncher.Converters;

/// <summary>Binds a group of RadioButtons to one enum: each is checked when the value's name matches its parameter, and checking it sets that value.</summary>
public class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value != null && string.Equals(value.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter != null ? Enum.Parse(targetType, parameter.ToString()!, ignoreCase: true) : System.Windows.Data.Binding.DoNothing;
}
