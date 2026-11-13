using System.Windows;

namespace RepoNLauncher.Controls;

/// <summary>
/// Inverse video, the way a console shows a selection: the text and background colors swapped (dark text on a light
/// block in the dark theme, light on dark in the light one). Set on an element whose background is the Accent block (a
/// primary button), and the text inside takes Text.OnAccent. It's inherited, so it reaches the text in the element's
/// content and template, where the app-wide TextBlock style's own color would otherwise win.
/// </summary>
public static class InverseVideo
{
    public static readonly DependencyProperty IsOnProperty = DependencyProperty.RegisterAttached(
        "IsOn", typeof(bool), typeof(InverseVideo),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsOn(DependencyObject element) => (bool)element.GetValue(IsOnProperty);

    public static void SetIsOn(DependencyObject element, bool value) => element.SetValue(IsOnProperty, value);
}
