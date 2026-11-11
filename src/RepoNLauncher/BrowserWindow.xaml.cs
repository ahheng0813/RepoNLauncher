using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RepoNLauncher.Services;
using RepoNLauncher.ViewModels;
using Application = System.Windows.Application;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace RepoNLauncher;

/// <summary>
/// The site a project's run serves, in a browser of the dashboard's own: filling a screen of its own, another than the
/// dashboard's when there is one, or the screen picked in the Run tab. One window per project; running it again shows
/// the new run's site in the same window.
/// </summary>
public partial class BrowserWindow : Window
{
    private static readonly Dictionary<string, BrowserWindow> OpenWindows = new(StringComparer.OrdinalIgnoreCase);

    // Edge's data for the sites (their cookies and storage, so a sign-in lasts), beside the file preview's but apart from it.
    private static readonly string WebViewData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Path.GetFileName(DashboardConfigService.AppDataDir), "Browser");

    // A dev server can print its address a moment before it answers. Until it does, the window tries again every second
    // for this long, rather than showing the browser's error page.
    private static readonly TimeSpan WaitForServer = TimeSpan.FromSeconds(30);

    private readonly string _projectName;
    private readonly DispatcherTimer _retry = new() { Interval = TimeSpan.FromSeconds(1) };

    // The site the run serves, and until when a failure to reach it means it's still starting.
    private string _url;
    private DateTime _waitUntil;

    private bool _isLoading;
    private bool _webViewFailed;
    private bool _closed;
    private bool _isFullScreen;
    private WindowState _stateBeforeFullScreen = WindowState.Normal;
    private bool _maximizeOnceMade;

    private BrowserWindow(string projectName, string url)
    {
        InitializeComponent();
        _projectName = projectName;
        _url = url;
        Title = $"{projectName} - Repo'N'Launcher";
        Address.Text = url;
        Web.CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = WebViewData };
        _retry.Tick += (_, _) =>
        {
            _retry.Stop();
            Web.CoreWebView2?.Navigate(_url);
        };

        SourceInitialized += (_, _) =>
        {
            NativeMethods.UseImmersiveDarkMode(new WindowInteropHelper(this).Handle);
            if (_maximizeOnceMade) WindowState = WindowState.Maximized;
        };
        Loaded += async (_, _) =>
        {
            NextScreenButton.Visibility = Screen.AllScreens.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
            await StartAsync();
        };
    }

    /// <summary>Shows the site in the project's browser window, opening one on the screen asked for if it has none.</summary>
    public static void Open(SiteRequest request)
    {
        if (OpenWindows.TryGetValue(request.ProjectPath, out var open))
        {
            open.Go(request.Url);
            if (request.Activate) NativeMethods.BringToFront(open);
            else if (open.WindowState == WindowState.Minimized) SystemCommands.RestoreWindow(open);
            return;
        }

        var window = new BrowserWindow(request.ProjectName, request.Url);
        OpenWindows[request.ProjectPath] = window;
        window.Closed += (_, _) => OpenWindows.Remove(request.ProjectPath);
        window.PlaceOn(TargetScreen(request.Screen));
        window.Show();
    }

    /// <summary>Moves the open browser windows to the screen picked in the Run tab.</summary>
    public static void MoveAllTo(string? screen)
    {
        foreach (var window in OpenWindows.Values) window.PlaceOn(TargetScreen(screen));
    }

    /// <summary>Closes every browser window; they aren't the dashboard's own, so they'd keep the app open.</summary>
    public static void CloseAll()
    {
        foreach (var window in OpenWindows.Values.ToList()) window.Close();
    }

    // The screen picked, else another than the dashboard's, as a screen of its own is the place for the site; with one
    // screen, the dashboard's.
    private static Screen TargetScreen(string? device)
    {
        var screens = Screen.AllScreens;
        if (device != null && screens.FirstOrDefault(s => s.DeviceName == device) is { } picked) return picked;
        var dashboard = DashboardScreen();
        return screens.FirstOrDefault(s => s.DeviceName != dashboard.DeviceName) ?? dashboard;
    }

    private static Screen DashboardScreen() => Screen.FromHandle(new WindowInteropHelper(Application.Current.MainWindow).Handle);

    // On a screen of its own it fills it; on the dashboard's it leaves the dashboard showing around it.
    private void PlaceOn(Screen screen)
    {
        SetFullScreen(false);

        // The screen's size is in device pixels; the window's in the dashboard's units.
        var fromDevice = PresentationSource.FromVisual(Application.Current.MainWindow)?.CompositionTarget?.TransformFromDevice
                         ?? Matrix.Identity;
        var area = screen.WorkingArea;
        var topLeft = fromDevice.Transform(new Point(area.Left, area.Top));
        var size = fromDevice.Transform(new Vector(area.Width, area.Height));

        WindowState = WindowState.Normal;
        Width = Math.Round(size.X * 0.8);
        Height = Math.Round(size.Y * 0.85);
        Left = topLeft.X + (size.X - Width) / 2;
        Top = topLeft.Y + (size.Y - Height) / 2;

        // Maximized before it has a window of its own, it would fill the main screen rather than this one; it waits for the
        // window, made where it was just put.
        var fill = screen.DeviceName != DashboardScreen().DeviceName;
        if (new WindowInteropHelper(this).Handle != IntPtr.Zero)
        {
            if (fill) WindowState = WindowState.Maximized;
        }
        else
        {
            _maximizeOnceMade = fill;
        }
    }

    private async Task StartAsync()
    {
        try
        {
            await Web.EnsureCoreWebView2Async();
        }
        catch (Exception e) when (e is WebView2RuntimeNotFoundException or ExternalException or IOException or UnauthorizedAccessException
                                      or ArgumentException or InvalidOperationException)
        {
            // Closed while it was starting, or no Edge to show it with: the default browser shows the site instead.
            if (_closed) return;
            _webViewFailed = true;
            Web.Visibility = Visibility.Collapsed;
            ShowNote(e is WebView2RuntimeNotFoundException
                ? "This window needs Microsoft Edge WebView2, which this PC doesn't have, so the site opened in your default browser."
                : $"The site couldn't be shown here ({e.Message.TrimEnd('.')}), so it opened in your default browser.");
            OpenInDefaultBrowser(_url);
            return;
        }

        if (_closed) return;
        var web = Web.CoreWebView2;
        web.DocumentTitleChanged += (_, _) => Title = string.IsNullOrWhiteSpace(web.DocumentTitle)
            ? $"{_projectName} - Repo'N'Launcher"
            : $"{web.DocumentTitle} - {_projectName} - Repo'N'Launcher";
        web.SourceChanged += (_, _) =>
        {
            if (!Address.IsKeyboardFocusWithin) Address.Text = web.Source;
        };
        web.HistoryChanged += (_, _) =>
        {
            BackButton.IsEnabled = web.CanGoBack;
            ForwardButton.IsEnabled = web.CanGoForward;
        };
        web.NavigationStarting += (_, _) => SetLoading(true);
        web.NavigationCompleted += OnNavigationCompleted;
        // The page's own full screen, such as a video's, takes the window's with it.
        web.ContainsFullScreenElementChanged += (_, _) => SetFullScreen(web.ContainsFullScreenElement);
        // A dev server's HTTPS certificate is its own, which Edge doesn't trust unless it was set up to (ASP.NET's dev-certs,
        // say). Like Chrome's allow-insecure-localhost, that's let through for this PC's own servers only.
        web.ServerCertificateErrorDetected += (_, e) =>
        {
            if (Uri.TryCreate(e.RequestUri, UriKind.Absolute, out var uri) && uri.IsLoopback)
                e.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
        };

        Go(_url);
    }

    /// <summary>Shows the run's site, waiting a while for its server to answer.</summary>
    private void Go(string url)
    {
        _url = url;
        Address.Text = url;
        _waitUntil = DateTime.UtcNow + WaitForServer;
        _retry.Stop();

        if (_webViewFailed) OpenInDefaultBrowser(url);
        Web.CoreWebView2?.Navigate(url); // until it's ready, StartAsync goes there once it is
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        // While the server starts, a failure is most likely it not answering yet; Edge reports a refused connection as
        // Unknown, not CannotConnect. A cancelled one is a stop, or another page asked for.
        if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled && DateTime.UtcNow < _waitUntil)
        {
            Web.Visibility = Visibility.Hidden;
            ShowNote($"Waiting for {_url} to answer…");
            _retry.Start();
            SetLoading(true);
            return;
        }

        // Once it has answered (or given up waiting), a failure shows as it is: the server stopped, say.
        _waitUntil = DateTime.MinValue;
        WaitingNote.Visibility = Visibility.Collapsed;
        Web.Visibility = Visibility.Visible;
        SetLoading(false);
    }

    private void ShowNote(string text)
    {
        WaitingText.Text = text;
        WaitingNote.Visibility = Visibility.Visible;
    }

    // While a page loads, or the window waits for the server, Reload is Stop.
    private void SetLoading(bool loading)
    {
        _isLoading = loading;
        ReloadButton.Content = loading ? "" : "";
        ReloadButton.ToolTip = loading ? "Stop" : "Reload (F5)";
        AutomationProperties.SetName(ReloadButton, loading ? "Stop" : "Reload");
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        var web = Web.CoreWebView2;
        if (web == null) return;

        if (!_isLoading)
        {
            web.Reload();
            return;
        }

        // Stopping a wait shows the browser's own page for the address it couldn't reach.
        var waiting = _retry.IsEnabled;
        _retry.Stop();
        _waitUntil = DateTime.MinValue;
        web.Stop();
        if (waiting)
        {
            WaitingNote.Visibility = Visibility.Collapsed;
            Web.Visibility = Visibility.Visible;
            SetLoading(false);
            web.Navigate(_url);
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e) => Web.CoreWebView2?.GoBack();

    private void Forward_Click(object sender, RoutedEventArgs e) => Web.CoreWebView2?.GoForward();

    private void DevTools_Click(object sender, RoutedEventArgs e) => Web.CoreWebView2?.OpenDevToolsWindow();

    private void OpenInDefaultBrowser_Click(object sender, RoutedEventArgs e) => OpenInDefaultBrowser(Web.CoreWebView2?.Source ?? _url);

    private static void OpenInDefaultBrowser(string url)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void NextScreen_Click(object sender, RoutedEventArgs e)
    {
        var screens = Screen.AllScreens.OrderBy(s => s.Bounds.Left).ThenBy(s => s.Bounds.Top).ToList();
        var current = Screen.FromHandle(new WindowInteropHelper(this).Handle);
        var index = screens.FindIndex(s => s.DeviceName == current.DeviceName);
        PlaceOn(screens[(index + 1) % screens.Count]);
    }


    private void FullScreen_Click(object sender, RoutedEventArgs e) => SetFullScreen(!_isFullScreen);

    // Typed without a scheme, such as localhost:3000, it's taken as http.
    private void Address_KeyDown(object sender, KeyEventArgs e)
    {
        var web = Web.CoreWebView2;
        if (e.Key == Key.Escape)
        {
            if (web != null) Address.Text = web.Source;
            Web.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && web != null)
        {
            var text = Address.Text.Trim();
            if (text.Length == 0) return;
            if (!text.Contains("://")) text = "http://" + text;
            try
            {
                _waitUntil = DateTime.MinValue;
                web.Navigate(text);
                Web.Focus();
            }
            catch (ArgumentException)
            {
                Address.SelectAll();
            }
            e.Handled = true;
        }
    }

    private void Address_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        Dispatcher.BeginInvoke(() => Address.SelectAll(), DispatcherPriority.Input);

    // The page has the keyboard most of the time; it passes these keys on, as it does F5, F12 and Alt+arrows to Edge.
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.F11)
        {
            SetFullScreen(!_isFullScreen);
            e.Handled = true;
        }
        else if (key == Key.Escape && _isFullScreen && Web.CoreWebView2?.ContainsFullScreenElement != true)
        {
            SetFullScreen(false);
            e.Handled = true;
        }
        else if ((key == Key.L && Keyboard.Modifiers == ModifierKeys.Control) || (key == Key.D && Keyboard.Modifiers == ModifierKeys.Alt))
        {
            Address.Focus();
            e.Handled = true;
        }
    }

    private void SetFullScreen(bool on)
    {
        if (on == _isFullScreen) return;
        _isFullScreen = on;

        if (on)
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
    }

    private void RevealStrip_MouseEnter(object sender, MouseEventArgs e)
    {
        Toolbar.Visibility = Visibility.Visible;
        RevealStrip.Visibility = Visibility.Collapsed;
    }

    private void Toolbar_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_isFullScreen || Address.IsKeyboardFocusWithin) return;
        Toolbar.Visibility = Visibility.Collapsed;
        RevealStrip.Visibility = Visibility.Visible;
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _retry.Stop();
        Web.Dispose();
        base.OnClosed(e);
    }
}
