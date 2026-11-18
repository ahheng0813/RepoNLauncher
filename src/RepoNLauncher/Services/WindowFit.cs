using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Point = System.Windows.Point;
using Screen = System.Windows.Forms.Screen;

namespace RepoNLauncher.Services;

/// <summary>
/// Keeps a window that opens bigger than its screen on that screen. Windows are sized for a large monitor; on a laptop,
/// or with Windows' text scaling turned up, one could open taller or wider than the screen, its title bar or the buttons
/// along its bottom out of sight or under the taskbar. Such a window is shrunk to the screen's work area and moved onto
/// it. A window that fits is left where it was put.
/// </summary>
public static class WindowFit
{
    /// <summary>Fits every window the app opens, as it opens.</summary>
    public static void Register() =>
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window && ReferenceEquals(e.OriginalSource, window)) FitToScreen(window);
    }

    public static void FitToScreen(Window window)
    {
        if (window.WindowState != WindowState.Normal) return;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        // The screen's work area is in device pixels; the window's size in its own units.
        var fromDevice = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var device = Screen.FromHandle(hwnd).WorkingArea;
        var area = new Rect(fromDevice.Transform(new Point(device.Left, device.Top)),
            fromDevice.Transform(new System.Windows.Vector(device.Width, device.Height)));

        var width = window.ActualWidth;
        var height = window.ActualHeight;
        if (width <= area.Width && height <= area.Height) return;

        // Its smallest size can't be more than the screen either, or it would stay too big.
        if (window.MinWidth > area.Width) window.MinWidth = area.Width;
        if (window.MinHeight > area.Height) window.MinHeight = area.Height;
        width = Math.Min(width, area.Width);
        height = Math.Min(height, area.Height);
        window.Width = width;
        window.Height = height;
        window.Left = Math.Clamp(window.Left, area.Left, area.Right - width);
        window.Top = Math.Clamp(window.Top, area.Top, area.Bottom - height);
    }
}
