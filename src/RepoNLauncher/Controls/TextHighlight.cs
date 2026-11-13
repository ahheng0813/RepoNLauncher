using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace RepoNLauncher.Controls;

/// <summary>
/// Shows <see cref="TextProperty"/> in a TextBlock with every occurrence of <see cref="MatchProperty"/> marked,
/// ignoring case: where a search found its text.
/// </summary>
public static class TextHighlight
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(TextHighlight), new PropertyMetadata(null, Update));

    public static readonly DependencyProperty MatchProperty = DependencyProperty.RegisterAttached(
        "Match", typeof(string), typeof(TextHighlight), new PropertyMetadata(null, Update));

    public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);
    public static void SetText(DependencyObject element, string? value) => element.SetValue(TextProperty, value);

    public static string? GetMatch(DependencyObject element) => (string?)element.GetValue(MatchProperty);
    public static void SetMatch(DependencyObject element, string? value) => element.SetValue(MatchProperty, value);

    private static void Update(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not TextBlock block) return;

        var text = GetText(block) ?? string.Empty;
        var match = GetMatch(block);
        block.Inlines.Clear();

        var start = 0;
        if (!string.IsNullOrEmpty(match))
        {
            for (var at = text.IndexOf(match, StringComparison.OrdinalIgnoreCase); at >= 0;
                 at = text.IndexOf(match, start, StringComparison.OrdinalIgnoreCase))
            {
                if (at > start) block.Inlines.Add(new Run(text[start..at]));
                // Resource references, so the mark follows a change of theme
                var found = new Run(text.Substring(at, match.Length));
                found.SetResourceReference(TextElement.BackgroundProperty, "Warning");
                found.SetResourceReference(TextElement.ForegroundProperty, "Bg.App");
                block.Inlines.Add(found);
                start = at + match.Length;
            }
        }
        if (start < text.Length) block.Inlines.Add(new Run(text[start..]));
    }
}
