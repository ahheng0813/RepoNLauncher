using System.Windows;

namespace RepoNLauncher.Controls;

/// <summary>
/// Inverse video, the way a console shows a selection: dark text on a light block. Set on an element with a light
/// background (a primary button), and the text inside turns dark. It's inherited, so it reaches the text in the
/// element's content and template, where the app-wide TextBlock style's own color would otherwise win.
/// </summary>
public static class InverseVideo
{
    public static readonly DependencyProperty IsOnProperty = DependencyProperty.RegisterAttached(
        "IsOn", typeof(bool), typeof(InverseVideo),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsOn(DependencyObject element) => (bool)element.GetValue(IsOnProperty);

    public static void SetIsOn(DependencyObject element, bool value) => element.SetValue(IsOnProperty, value);
}
