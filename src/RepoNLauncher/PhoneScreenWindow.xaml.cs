using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using RepoNLauncher.ViewModels;

namespace RepoNLauncher;

/// <summary>The phone screen popped out of the dashboard into its own window, which can go borderless full screen.</summary>
public partial class PhoneScreenWindow : Window
{
    private const int HotkeyToggleFullScreen = 1;
    private const int HotkeyExitFullScreen = 2;

    private readonly PhoneScreenViewModel _phone;
    private WindowState _stateBeforeFullScreen = WindowState.Normal;

    public PhoneScreenWindow(PhoneScreenViewModel phone)
    {
        InitializeComponent();
        DataContext = _phone = phone;
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 40);

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.UseImmersiveDarkMode(hwnd);
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        };
        Activated += (_, _) => UpdateHotkeys();
        Deactivated += (_, _) => UpdateHotkeys();
        Closing += (_, _) =>
        {
            phone.PropertyChanged -= OnPhonePropertyChanged;
            UnregisterHotkeys(new WindowInteropHelper(this).Handle);
        };
        Closed += (_, _) =>
        {
            // Closing doesn't destroy the screen host; detach its binding and destroy it, or it lingers
            // and competes with the next pop-out's host for the screen.
            DataContext = null;
            Screen.Dispose();
        };

        phone.PropertyChanged += OnPhonePropertyChanged;
        if (phone.IsFullScreen) ApplyFullScreen();
    }

    private void OnPhonePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PhoneScreenViewModel.IsFullScreen)) ApplyFullScreen();
    }

    private void ApplyFullScreen()
    {
        if (_phone.IsFullScreen)
        {
            _stateBeforeFullScreen = WindowState;
            // Maximizing only after dropping the frame is what makes the window cover the taskbar too.
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            Toolbar.Visibility = Visibility.Collapsed;
            RevealStrip.Visibility = Visibility.Visible;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _stateBeforeFullScreen;
            Toolbar.Visibility = Visibility.Visible;
            RevealStrip.Visibility = Visibility.Collapsed;
        }
        UpdateHotkeys();
    }

    private void RevealStrip_MouseEnter(object sender, MouseEventArgs e)
    {
        Toolbar.Visibility = Visibility.Visible;
        RevealStrip.Visibility = Visibility.Collapsed;
    }

    private void Toolbar_MouseLeave(object sender, MouseEventArgs e)
    {
        // Moving into a tool's flyout leaves the toolbar too; hiding it then would take the flyout with it.
        if (!_phone.IsFullScreen || Tools.IsFlyoutOpen) return;
        Toolbar.Visibility = Visibility.Collapsed;
        RevealStrip.Visibility = Visibility.Visible;
    }

    // Once the mirror (another process's window) has keyboard focus, WPF never sees key presses, so F11/Esc
    // are registered as system hotkeys instead — but only while this window is the active one.
    private void UpdateHotkeys()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        UnregisterHotkeys(hwnd);
        if (!IsActive) return;

        RegisterHotKey(hwnd, HotkeyToggleFullScreen, ModNoRepeat, VkF11);
        if (_phone.IsFullScreen) RegisterHotKey(hwnd, HotkeyExitFullScreen, ModNoRepeat, VkEscape);
    }

    private static void UnregisterHotkeys(IntPtr hwnd)
    {
        UnregisterHotKey(hwnd, HotkeyToggleFullScreen);
        UnregisterHotKey(hwnd, HotkeyExitFullScreen);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey) return IntPtr.Zero;

        switch (wParam.ToInt32())
        {
            case HotkeyToggleFullScreen:
                _phone.IsFullScreen = !_phone.IsFullScreen;
                handled = true;
                break;
            case HotkeyExitFullScreen:
                _phone.IsFullScreen = false;
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkEscape = 0x1B;
    private const uint VkF11 = 0x7A;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
