using System.ComponentModel;
using System.Globalization;
using RepoNLauncher.Models;

namespace RepoNLauncher.Services;

/// <summary>
/// Dates and times as Settings shows them: commit dates, the phone's file dates, and the times in status lines.
/// XAML shows a date with <c>{conv:DateTimeText Date}</c>, which follows <see cref="FullPattern"/> so the text changes
/// as soon as the setting does.
/// </summary>
public static class DateTimeDisplay
{
    private static string? _datePattern;
    private static string? _timePattern;

    /// <summary>
    /// For a pattern picked in Settings: / and : stand for themselves rather than Windows' separators, and a 12-hour time
    /// always says AM or PM. Month and day names stay in Windows' language.
    /// </summary>
    private static readonly CultureInfo Literal = MakeLiteral();

    /// <summary>Raised for <see cref="FullPattern"/>; WPF bindings to the static property listen for it.</summary>
    public static event EventHandler<PropertyChangedEventArgs>? StaticPropertyChanged;

    public static string SystemDatePattern => CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern;
    public static string SystemTimePattern => CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern;

    /// <summary>The date and time patterns in use, for bindings to follow.</summary>
    public static string FullPattern => $"{_datePattern ?? SystemDatePattern} {_timePattern ?? SystemTimePattern}";

    public static void Apply(DateTimeFormatSettings? settings)
    {
        _datePattern = Usable(settings?.DatePattern);
        _timePattern = Usable(settings?.TimePattern);
        StaticPropertyChanged?.Invoke(null, new PropertyChangedEventArgs(nameof(FullPattern)));
    }

    public static string Date(DateTime value) => Format(value, _datePattern, SystemDatePattern);

    public static string Time(DateTime value) => Format(value, _timePattern, SystemTimePattern);

    public static string DateAndTime(DateTime value) => $"{Date(value)} {Time(value)}";

    /// <summary>In this PC's time zone, as every date on the dashboard is.</summary>
    public static string DateAndTime(DateTimeOffset value) => DateAndTime(value.LocalDateTime);

    /// <summary>The value in a date pattern that isn't in use yet; null for Windows' short date. Throws FormatException.</summary>
    public static string DateWith(DateTime value, string? pattern) => Format(value, pattern, SystemDatePattern);

    /// <summary>The value in a time pattern that isn't in use yet; null for Windows' short time. Throws FormatException.</summary>
    public static string TimeWith(DateTime value, string? pattern) => Format(value, pattern, SystemTimePattern);

    public static bool IsValid(string pattern)
    {
        try
        {
            _ = DateTime.Now.ToString(pattern, Literal);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Format(DateTime value, string? pattern, string systemPattern) => pattern == null
        ? value.ToString(systemPattern, CultureInfo.CurrentCulture)
        : value.ToString(pattern, Literal);

    // A blank or broken pattern in the config (edited by hand, say) falls back to Windows' format.
    private static string? Usable(string? pattern) => string.IsNullOrWhiteSpace(pattern) || !IsValid(pattern) ? null : pattern;

    private static CultureInfo MakeLiteral()
    {
        var culture = (CultureInfo)CultureInfo.CurrentCulture.Clone();
        culture.DateTimeFormat.DateSeparator = "/";
        culture.DateTimeFormat.TimeSeparator = ":";
        if (culture.DateTimeFormat.AMDesignator.Length == 0 || culture.DateTimeFormat.PMDesignator.Length == 0)
        {
            culture.DateTimeFormat.AMDesignator = "AM";
            culture.DateTimeFormat.PMDesignator = "PM";
        }
        return culture;
    }
}
