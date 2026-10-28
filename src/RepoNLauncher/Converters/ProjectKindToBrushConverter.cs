using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using RepoNLauncher.Models;

namespace RepoNLauncher.Converters;

/// <summary>Maps a ProjectKind to its badge color, used for the small kind pill in the project list.</summary>
public class ProjectKindToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Flutter = (SolidColorBrush)new BrushConverter().ConvertFrom("#0E8FA3")!;
    private static readonly SolidColorBrush DotNet = (SolidColorBrush)new BrushConverter().ConvertFrom("#7C3AED")!;
    private static readonly SolidColorBrush Node = (SolidColorBrush)new BrushConverter().ConvertFrom("#059669")!;
    private static readonly SolidColorBrush Python = (SolidColorBrush)new BrushConverter().ConvertFrom("#3776AB")!;
    private static readonly SolidColorBrush Unknown = (SolidColorBrush)new BrushConverter().ConvertFrom("#4B5563")!;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ProjectKind kind) return Unknown;
        return kind switch
        {
            ProjectKind.Flutter => Flutter,
            ProjectKind.DotNet => DotNet,
            ProjectKind.Node => Node,
            ProjectKind.Python => Python,
            _ => Unknown,
        };
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
