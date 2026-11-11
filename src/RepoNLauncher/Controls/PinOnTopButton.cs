using System.Windows;
using Button = System.Windows.Controls.Button;

namespace RepoNLauncher.Controls;

/// <summary>
/// The thumbtack in a floating window's header: pins the window it's in on top of other windows, and unpins it.
/// It looks like the theme's Button.Pin, lit while the window is pinned.
/// </summary>
public class PinOnTopButton : Button
{
    public PinOnTopButton() => SetResourceReference(StyleProperty, "Button.Pin");

    protected override void OnClick()
    {
        base.OnClick();
        if (Window.GetWindow(this) is { } window) window.Topmost = !window.Topmost;
    }
}
