using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using RepoNLauncher.Models;

namespace RepoNLauncher.Converters;

public class FileChangeTypeToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Modified = (SolidColorBrush)new BrushConverter().ConvertFrom("#F9F1A5")!;
    private static readonly SolidColorBrush Added = (SolidColorBrush)new BrushConverter().ConvertFrom("#16C60C")!;
    private static readonly SolidColorBrush Deleted = (SolidColorBrush)new BrushConverter().ConvertFrom("#E74856")!;
    private static readonly SolidColorBrush Renamed = (SolidColorBrush)new BrushConverter().ConvertFrom("#61D6D6")!;
    private static readonly SolidColorBrush Untracked = (SolidColorBrush)new BrushConverter().ConvertFrom("#767676")!;
    private static readonly SolidColorBrush Conflicted = (SolidColorBrush)new BrushConverter().ConvertFrom("#E74856")!;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not FileChangeType type) return Untracked;
        return type switch
        {
            FileChangeType.Modified => Modified,
            FileChangeType.Added => Added,
            FileChangeType.Deleted => Deleted,
            FileChangeType.Renamed => Renamed,
            FileChangeType.Copied => Renamed,
            FileChangeType.Conflicted => Conflicted,
            _ => Untracked,
        };
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
