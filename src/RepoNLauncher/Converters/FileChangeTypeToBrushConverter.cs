using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using RepoNLauncher.Models;

namespace RepoNLauncher.Converters;

public class FileChangeTypeToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Modified = (SolidColorBrush)new BrushConverter().ConvertFrom("#FBBF24")!;
    private static readonly SolidColorBrush Added = (SolidColorBrush)new BrushConverter().ConvertFrom("#34D399")!;
    private static readonly SolidColorBrush Deleted = (SolidColorBrush)new BrushConverter().ConvertFrom("#F87171")!;
    private static readonly SolidColorBrush Renamed = (SolidColorBrush)new BrushConverter().ConvertFrom("#22D3EE")!;
    private static readonly SolidColorBrush Untracked = (SolidColorBrush)new BrushConverter().ConvertFrom("#6B7280")!;
    private static readonly SolidColorBrush Conflicted = (SolidColorBrush)new BrushConverter().ConvertFrom("#F87171")!;

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
