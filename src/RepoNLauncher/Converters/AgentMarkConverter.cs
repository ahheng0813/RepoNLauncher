using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace RepoNLauncher.Converters;

/// <summary>
/// Maps a tool, by name ("Claude", "Codex") or AgentKind, to its mark: the ClaudeMark or CodexMark geometry in the theme.
/// Anything else gets no mark.
/// </summary>
public class AgentMarkConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() switch
        {
            "Claude" => System.Windows.Application.Current.TryFindResource("ClaudeMark") as Geometry,
            "Codex" => System.Windows.Application.Current.TryFindResource("CodexMark") as Geometry,
            _ => null,
        };

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
