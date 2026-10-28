using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace RepoNLauncher.Controls;

/// <summary>
/// Embeds another process's top-level window (the scrcpy mirror) as a child of this element.
/// Hosts in different windows can take turns owning the same window: whichever host is given a
/// non-zero <see cref="ScreenHandle"/> re-parents it into itself, so the screen can move between
/// the docked panel and the pop-out window without restarting scrcpy.
/// </summary>
public class ScreenHost : HwndHost
{
    public static readonly DependencyProperty ScreenHandleProperty = DependencyProperty.Register(
        nameof(ScreenHandle), typeof(IntPtr), typeof(ScreenHost),
        new PropertyMetadata(IntPtr.Zero, (d, _) => ((ScreenHost)d).Adopt()));

    public IntPtr ScreenHandle
    {
        get => (IntPtr)GetValue(ScreenHandleProperty);
        set => SetValue(ScreenHandleProperty, value);
    }

    private readonly DispatcherTimer _fitTimer;
    private IntPtr _container;
    private IntPtr _child;

    public ScreenHost()
    {
        // scrcpy moves/resizes its own window (when the first frame arrives, and on device rotation),
        // which sends no message to us — keep pulling it back to fill the host.
        _fitTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _fitTimer.Tick += (_, _) => FitChild();
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _container = CreateWindowEx(0, "static", null, WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | SS_BLACKRECT,
            0, 0, 0, 0, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Adopt();
        return new HandleRef(this, _container);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        // Destroying a window destroys its children too, even ones owned by another process.
        Release();
        DestroyWindow(hwnd.Handle);
        _container = IntPtr.Zero;
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_SIZE)
        {
            FitChild();
        }
        else if (msg == WM_PARENTNOTIFY && _child != IntPtr.Zero &&
                 (wParam.ToInt64() & 0xFFFF) is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN)
        {
            // A click in the mirror doesn't move keyboard focus into it on its own, so typing would never reach the phone.
            SetFocus(_child);
        }
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    private void Adopt()
    {
        var target = ScreenHandle;
        if (_child != IntPtr.Zero && _child != target) Release();
        if (_container == IntPtr.Zero || target == IntPtr.Zero || !IsWindow(target)) return;

        // A host whose window has closed isn't destroyed — WPF parks it on a hidden window with its bindings
        // still live. It must never take the screen, or the screen vanishes into that hidden window.
        if (PresentationSource.FromVisual(this) == null) return;

        if (GetParent(target) != _container)
        {
            var style = GetWindowLong(target, GWL_STYLE);
            SetWindowLong(target, GWL_STYLE, (style & ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME)) | WS_CHILD);
            SetParent(target, _container);
        }

        _child = target;
        FitChild();
        ShowWindow(target, SW_SHOWNA);
        _fitTimer.Start();
    }

    private void Release()
    {
        _fitTimer.Stop();
        var child = _child;
        _child = IntPtr.Zero;

        // Another host may already have adopted it; only hand back a window that's still ours.
        if (child == IntPtr.Zero || !IsWindow(child) || GetParent(child) != _container) return;

        // Park it hidden on the desktop until the next host adopts it.
        ShowWindow(child, SW_HIDE);
        SetParent(child, IntPtr.Zero);
        SetWindowLong(child, GWL_STYLE, (GetWindowLong(child, GWL_STYLE) & ~WS_CHILD) | WS_POPUP);
    }

    private void FitChild()
    {
        if (_child == IntPtr.Zero || _container == IntPtr.Zero) return;
        if (!IsWindow(_child))
        {
            // scrcpy exited; its window went with it.
            _child = IntPtr.Zero;
            _fitTimer.Stop();
            return;
        }

        GetClientRect(_container, out var bounds);
        GetWindowRect(_child, out var current);
        MapWindowPoints(IntPtr.Zero, _container, ref current, 2);
        if (!current.Equals(bounds)) MoveWindow(_child, 0, 0, bounds.Right, bounds.Bottom, true);
    }

    private const int GWL_STYLE = -16;
    private const int WS_CHILD = 0x40000000;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPCHILDREN = 0x02000000;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int SS_BLACKRECT = 0x4;
    private const int SW_HIDE = 0;
    private const int SW_SHOWNA = 8;
    private const int WM_SIZE = 0x0005;
    private const int WM_PARENTNOTIFY = 0x0210;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;

    [StructLayout(LayoutKind.Sequential)]
    private record struct Rect(int Left, int Top, int Right, int Bottom);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string? windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool MoveWindow(IntPtr hwnd, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern int MapWindowPoints(IntPtr from, IntPtr to, ref Rect points, int count);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hwnd);
}
