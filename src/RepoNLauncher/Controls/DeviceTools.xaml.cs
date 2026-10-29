using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UserControl = System.Windows.Controls.UserControl;
using RepoNLauncher.Services;
using RepoNLauncher.ViewModels;

namespace RepoNLauncher.Controls;

/// <summary>
/// The Phone screen's tool buttons, with the recordings in progress and the file saved last under them. Files dropped on
/// it are sent to the phone.
/// </summary>
public partial class DeviceTools : UserControl
{
    private const int WmClose = 0x0010;
    private const int WmSysCommand = 0x0112;
    private const int ScClose = 0xF060;

    private HwndSource? _windowSource;

    public DeviceTools()
    {
        InitializeComponent();
        AllowDrop = true;
        DragOver += OnDragOver;
        Drop += OnDrop;

        Loaded += (_, _) =>
        {
            _windowSource = PresentationSource.FromVisual(this) as HwndSource;
            _windowSource?.AddHook(WndProc);
        };
        Unloaded += (_, _) =>
        {
            _windowSource?.RemoveHook(WndProc);
            _windowSource = null;
        };
    }

    public bool IsFlyoutOpen => CameraPopup.IsOpen || LocationPopup.IsOpen;

    // A flyout that closes on a click elsewhere holds the mouse while it's open, and the window then ignores being closed
    // (Alt+F4, or Close on the taskbar). Closing the flyouts first lets it close.
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmClose || (msg == WmSysCommand && (wParam.ToInt64() & 0xFFF0) == ScClose))
        {
            CameraPopup.IsOpen = false;
            LocationPopup.IsOpen = false;
        }
        return IntPtr.Zero;
    }

    private void CameraButton_Click(object sender, RoutedEventArgs e) => CameraPopup.IsOpen = true;

    private void CameraChoice_Click(object sender, RoutedEventArgs e) => CameraPopup.IsOpen = false;

    private void LocationButton_Click(object sender, RoutedEventArgs e)
    {
        LocationPopup.IsOpen = true;
        Dispatcher.BeginInvoke(() => Keyboard.Focus(SearchBox), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || LocationPopup.DataContext is not DeviceLocationViewModel location) return;
        if (location.SearchCommand.CanExecute(null)) location.SearchCommand.Execute(null);
        e.Handled = true;
    }

    private void CoordinatesBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || LocationPopup.DataContext is not DeviceLocationViewModel location) return;
        if (location.SetCommand.CanExecute(null)) location.SetCommand.Execute(null);
        e.Handled = true;
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Map.ZoomBy(1);

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Map.ZoomBy(-1);

    private void MapCopyright_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(OpenStreetMap.CopyrightUrl) { UseShellExecute = true });

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (DataContext is PhoneScreenViewModel phone && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            _ = phone.SendFilesAsync(paths);
    }
}
