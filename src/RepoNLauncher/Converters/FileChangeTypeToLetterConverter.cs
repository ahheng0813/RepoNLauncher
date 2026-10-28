using System.Globalization;
using System.Windows.Data;
using RepoNLauncher.Models;

namespace RepoNLauncher.Converters;

public class FileChangeTypeToLetterConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not FileChangeType type) return "?";
        return type switch
        {
            FileChangeType.Modified => "M",
            FileChangeType.Added => "A",
            FileChangeType.Deleted => "D",
            FileChangeType.Renamed => "R",
            FileChangeType.Copied => "C",
            FileChangeType.Conflicted => "!",
            FileChangeType.Untracked => "U",
            _ => "?",
        };
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
