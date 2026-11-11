using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Binding = System.Windows.Data.Binding;
using RepoNLauncher.Services;

namespace RepoNLauncher.Converters;

/// <summary>
/// Binds to a date (a DateTime or DateTimeOffset) and shows it the way Settings says, changing with the setting:
/// <c>Text="{conv:DateTimeText Date}"</c>. An unknown date (MinValue) shows as nothing.
/// </summary>
public class DateTimeText : MultiBinding
{
    private static readonly PropertyPath PatternPath =
        new("(0)", typeof(DateTimeDisplay).GetProperty(nameof(DateTimeDisplay.FullPattern))!);

    public DateTimeText() : this(string.Empty) { }

    public DateTimeText(string path)
    {
        Bindings.Add(new Binding(path));
        Bindings.Add(new Binding { Path = PatternPath, Mode = BindingMode.OneWay });
        Converter = new DateTimeTextConverter();
        Mode = BindingMode.OneWay;
    }
}

public class DateTimeTextConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) => values.FirstOrDefault() switch
    {
        DateTimeOffset offset when offset != DateTimeOffset.MinValue => DateTimeDisplay.DateAndTime(offset),
        DateTime time when time != DateTime.MinValue => DateTimeDisplay.DateAndTime(time),
        _ => string.Empty,
    };

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
