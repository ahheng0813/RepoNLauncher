using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RepoNLauncher.Models;
using RepoNLauncher.Services;
using RepoNLauncher.ViewModels;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace RepoNLauncher;

/// <summary>
/// The site a project's run serves, in a browser of the dashboard's own: filling a screen of its own, another than the
/// dashboard's when there is one, or the screen picked in the Run tab. One window per project; running it again shows
/// the new run's site in the same window. One more is no project's, opened from the dashboard's header to browse
/// without running anything. Opened without a site, a window starts on a page of the saved sites, which every window
/// also shows in a bar under its address.
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

    private readonly SiteBrowserViewModel _sites;

    // The project's name, or Browser for the window of no project's.
    private readonly string _label;
    private readonly DispatcherTimer _retry = new() { Interval = TimeSpan.FromSeconds(1) };

    // The site the run serves (or the one last typed or saved), and until when a failure to reach it means it's still
    // starting. Empty while the window shows its start page.
    private string _url;
    private DateTime _waitUntil;

    private bool _isLoading;
    private bool _webViewFailed;
    private bool _closed;
    private bool _isFullScreen;
    private WindowState _stateBeforeFullScreen = WindowState.Normal;
    private bool _maximizeOnceMade;

    // The saved site the popup edits, and whether closing it keeps what was typed (not after Remove, or Esc).
    private SavedSite? _editing;
    private bool _keepEdits;

    // Where the popup lines up with what it opened under: its right edge (the star, at the window's right) or its left.
    private bool _popupAlignRight;

    // Settings' Incognito as the window opened: it keeps nothing on this PC. Its browser can't change that once made.
    private readonly bool _incognito;

    private readonly AdBlockerViewModel _adBlock;

    // The page shown (or being gone to): its host, whether the ad blocker blocks on it, and how many it has blocked there.
    private string _pageHost = string.Empty;
    private bool _filterPage;
    private int _blocked;

    // Requests are only handed to the window to check while the ad blocker is on.
    private bool _checkingRequests;

    // Hiding the page's ads: the CSS a script added for each new page puts on it, that script once it's added, and whether
    // the page started loading before it was, so it's run on the page once that has loaded.
    private string? _hideCss;
    private string? _hideScriptId;
    private bool _hideLate;
    private Task _hiding = Task.CompletedTask;

    // Names what the hiding script leaves on the page, so pages can't tell it by name (it's new each run).
    private static readonly string HideKey = "_" + Guid.NewGuid().ToString("N")[..10];

    private BrowserWindow(SiteBrowserViewModel sites, string? projectName, string? url)
    {
        InitializeComponent();
        _sites = sites;
        _adBlock = sites.AdBlocker;
        _incognito = sites.IsIncognito;
        DataContext = sites;
        _label = projectName ?? "Browser";
        _url = url ?? string.Empty;
        Title = WindowTitle(null);
        IncognitoBadge.Visibility = _incognito ? Visibility.Visible : Visibility.Collapsed;
        Address.Text = _url;
        if (url != null)
        {
            _waitUntil = DateTime.UtcNow + WaitForServer;
        }
        else
        {
            // Nothing to show yet: the start page until an address is typed or a saved site picked.
            StartPage.Visibility = Visibility.Visible;
            Web.Visibility = Visibility.Hidden;
        }
        _sites.SavedSites.CollectionChanged += OnSavedSitesChanged;
        _adBlock.Changed += OnAdBlockChanged;

        // Placed by hand: Bottom placement follows the pen-tablet "right-handed" setting, which flips popups to the left.
        // The callback works in device pixels.
        SitePopup.CustomPopupPlacementCallback = (popupSize, targetSize, _) => PlaceUnder(popupSize, targetSize, _popupAlignRight);
        ShieldPopup.CustomPopupPlacementCallback = (popupSize, targetSize, _) => PlaceUnder(popupSize, targetSize, alignRight: true);
        UpdateShield();
        _retry.Tick += (_, _) =>
        {
            _retry.Stop();
            TryNavigate(_url);
        };

        SourceInitialized += (_, _) =>
        {
            NativeMethods.UseImmersiveDarkMode(new WindowInteropHelper(this).Handle);
            if (_maximizeOnceMade) WindowState = WindowState.Maximized;
        };
        Loaded += async (_, _) =>
        {
            NextScreenButton.Visibility = Screen.AllScreens.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
            if (StartPage.Visibility == Visibility.Visible) Address.Focus();
            await StartAsync();
        };
    }

    /// <summary>
    /// Shows the site in the project's browser window (or the one of no project's), opening one on the screen asked for
    /// if it has none; without a site, it just opens, on its start page.
    /// </summary>
    public static void Open(SiteBrowserViewModel sites, SiteRequest request)
    {
        var key = request.ProjectPath ?? string.Empty;
        if (OpenWindows.TryGetValue(key, out var open))
        {
            if (request.Url != null) open.Go(request.Url);
            if (request.Activate) NativeMethods.BringToFront(open);
            else if (open.WindowState == WindowState.Minimized) SystemCommands.RestoreWindow(open);
            return;
        }

        var window = new BrowserWindow(sites, request.ProjectName, request.Url);
        OpenWindows[key] = window;
        window.Closed += (_, _) => OpenWindows.Remove(key);
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

    private CustomPopupPlacement[] PlaceUnder(System.Windows.Size popupSize, System.Windows.Size targetSize, bool alignRight)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return [new CustomPopupPlacement(
            new Point(alignRight ? targetSize.Width - popupSize.Width : 0, targetSize.Height + 4 * dpi.DpiScaleY),
            PopupPrimaryAxis.Horizontal)];
    }

    private string WindowTitle(string? pageTitle)
    {
        var label = _incognito ? $"{_label} (Incognito)" : _label;
        return string.IsNullOrWhiteSpace(pageTitle) ? $"{label} - Repo'N'Launcher" : $"{pageTitle} - {label} - Repo'N'Launcher";
    }

    // Every browser window's environment is made the same way, as Edge needs for windows sharing a data folder.
    private static Task<CoreWebView2Environment> CreateEnvironmentAsync() => CoreWebView2Environment.CreateAsync(null, WebViewData);

    /// <summary>
    /// Clears what the dashboard browser has kept on this PC: history, cookies and sign-ins, form entries, site storage
    /// and the cache. A hidden browser in <paramref name="host"/> does it, so it works with no browser window open.
    /// </summary>
    public static async Task ClearBrowsingDataAsync(Window host)
    {
        var environment = await CreateEnvironmentAsync();
        var controller = await environment.CreateCoreWebView2ControllerAsync(new WindowInteropHelper(host).Handle);
        try
        {
            controller.IsVisible = false;
            await controller.CoreWebView2.Profile.ClearBrowsingDataAsync();
        }
        finally
        {
            controller.Close();
        }
    }

    private async Task StartAsync()
    {
        try
        {
            // Incognito keeps the window's browsing data in memory, so none of it reaches the data folder.
            var environment = await CreateEnvironmentAsync();
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.IsInPrivateModeEnabled = _incognito;
            await Web.EnsureCoreWebView2Async(environment, options);
        }
        catch (Exception e) when (e is WebView2RuntimeNotFoundException or ExternalException or IOException or UnauthorizedAccessException
                                      or ArgumentException or InvalidOperationException)
        {
            // Closed while it was starting, or no Edge to show it with: the default browser shows the site instead.
            if (_closed) return;
            _webViewFailed = true;
            Web.Visibility = Visibility.Collapsed;
            StartPage.Visibility = Visibility.Collapsed;
            var opened = _url.Length > 0 ? "the site opened" : "sites open";
            ShowNote(e is WebView2RuntimeNotFoundException
                ? $"This window needs Microsoft Edge WebView2, which this PC doesn't have, so {opened} in your default browser."
                : $"Sites couldn't be shown here ({e.Message.TrimEnd('.')}), so {opened} in your default browser.");
            OpenInDefaultBrowser(_url);
            return;
        }

        if (_closed) return;
        var web = Web.CoreWebView2;
        web.DocumentTitleChanged += (_, _) => Title = WindowTitle(web.DocumentTitle);
        web.SourceChanged += (_, _) =>
        {
            if (!Address.IsKeyboardFocusWithin) Address.Text = web.Source;
            UpdateStar();
        };
        web.HistoryChanged += (_, _) =>
        {
            BackButton.IsEnabled = web.CanGoBack;
            ForwardButton.IsEnabled = web.CanGoForward;
        };
        web.NavigationStarting += OnNavigationStarting;
        web.NavigationCompleted += OnNavigationCompleted;
        web.WebResourceRequested += OnWebResourceRequested;
        web.NewWindowRequested += OnNewWindowRequested;
        web.ContentLoading += (_, _) => _hideLate = _hideCss != null && _hideScriptId == null;
        web.DOMContentLoaded += (_, _) =>
        {
            if (_hideLate && _hideCss != null) _ = RunQuietlyAsync(web.ExecuteScriptAsync(HideScript(_hideCss)));
            _hideLate = false;
        };
        // The page's own full screen, such as a video's, takes the window's with it.
        web.ContainsFullScreenElementChanged += (_, _) => SetFullScreen(web.ContainsFullScreenElement);
        // A dev server's HTTPS certificate is its own, which Edge doesn't trust unless it was set up to (ASP.NET's dev-certs,
        // say). Like Chrome's allow-insecure-localhost, that's let through for this PC's own servers only.
        web.ServerCertificateErrorDetected += (_, e) =>
        {
            if (Uri.TryCreate(e.RequestUri, UriKind.Absolute, out var uri) && uri.IsLoopback)
                e.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
        };

        // The ad blocker's rules, read once for every window (on another thread, while this one's browser started), so
        // the first page is filtered too.
        await _adBlock.WhenReadyAsync();
        if (_closed) return;
        UpdateRequestChecking();

        // The run's site it was opened with, or an address typed or picked while it was starting.
        if (_url.Length > 0 && !TryNavigate(_url)) ShowStartPage();
    }

    // ---- Ad blocker ----

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        SetLoading(true);
        // A redirect is still the page asked for, so what was blocked on the way counts towards it.
        if (!e.IsRedirected) _blocked = 0;
        _pageHost = Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : string.Empty;
        UpdatePage();
    }

    // The page's site changed, or the ad blocker did: whether it blocks on the page, what it hides there, and the shield.
    private void UpdatePage()
    {
        _filterPage = _adBlock.Filters(_pageHost);
        _hiding = UpdateHidingAsync();
        UpdateShield();
    }

    private void OnAdBlockChanged()
    {
        if (_closed) return;
        UpdateRequestChecking();
        UpdatePage();
    }

    private void UpdateRequestChecking()
    {
        var web = Web.CoreWebView2;
        if (web == null || _adBlock.IsEnabled == _checkingRequests) return;
        _checkingRequests = _adBlock.IsEnabled;
        if (_checkingRequests) web.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        else web.RemoveWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!_filterPage) return;
        var url = e.Request.Uri;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "ws" or "wss")) return;

        var host = uri.Host.ToLowerInvariant();
        var context = e.ResourceContext;
        // The page itself: an address typed or picked shows, even an ad server's.
        if (context == CoreWebView2WebResourceContext.Document && host == _pageHost) return;
        if (!_adBlock.Engine.ShouldBlock(url, host, _pageHost, TypeOf(context))) return;

        e.Response = Web.CoreWebView2.Environment.CreateWebResourceResponse(null, 403, "Blocked by the ad blocker", string.Empty);
        _blocked++;
        UpdateShield();
    }

    private static AdRequestType TypeOf(CoreWebView2WebResourceContext context) => context switch
    {
        CoreWebView2WebResourceContext.Document => AdRequestType.Subdocument,
        CoreWebView2WebResourceContext.Script => AdRequestType.Script,
        CoreWebView2WebResourceContext.Image => AdRequestType.Image,
        CoreWebView2WebResourceContext.Stylesheet => AdRequestType.Stylesheet,
        CoreWebView2WebResourceContext.Media => AdRequestType.Media,
        CoreWebView2WebResourceContext.Font => AdRequestType.Font,
        CoreWebView2WebResourceContext.XmlHttpRequest or CoreWebView2WebResourceContext.Fetch
            or CoreWebView2WebResourceContext.EventSource => AdRequestType.XmlHttpRequest,
        CoreWebView2WebResourceContext.Websocket => AdRequestType.WebSocket,
        CoreWebView2WebResourceContext.Ping or CoreWebView2WebResourceContext.CspViolationReport => AdRequestType.Ping,
        _ => AdRequestType.Other,
    };

    // Pop-ups the lists know as ads, and on Strict any the page opens without a click.
    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (!_filterPage) return;
        var unasked = _adBlock.Level == AdBlockLevel.Strict && !e.IsUserInitiated;
        var listed = Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)
                     && _adBlock.Engine.ShouldBlockPopup(e.Uri, uri.Host.ToLowerInvariant(), _pageHost);
        if (!unasked && !listed) return;

        e.Handled = true;
        _blocked++;
        UpdateShield();
    }

    // The CSS for the page's site goes in a script run as each page starts, before its own scripts; it's added again
    // only when the CSS changes, as the site does. Added after the page has started, it's run once that has loaded.
    private async Task UpdateHidingAsync()
    {
        var web = Web.CoreWebView2;
        if (web == null) return;
        var css = _filterPage ? _adBlock.Engine.HidingCss(_pageHost) : null;
        if (css == _hideCss) return;

        _hideCss = css;
        if (_hideScriptId != null)
        {
            web.RemoveScriptToExecuteOnDocumentCreated(_hideScriptId);
            _hideScriptId = null;
        }
        if (css == null) return;

        string id;
        try
        {
            id = await web.AddScriptToExecuteOnDocumentCreatedAsync(HideScript(css));
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException or COMException)
        {
            return; // closed meanwhile
        }
        if (_closed) return;
        // Another site's CSS was asked for meanwhile: that one's script stands.
        if (_hideCss != css || _hideScriptId != null) web.RemoveScriptToExecuteOnDocumentCreated(id);
        else _hideScriptId = id;
    }

    // Adopted rather than put in a <style>, so a page whose security policy forbids inline styles still gets it, and its
    // own scripts don't come across it in the page. Only the page itself: an ad in a frame is blocked at its server.
    private static string HideScript(string css) => $$"""
        (() => {
          if (window.top !== window || window['{{HideKey}}']) return;
          const sheet = new CSSStyleSheet();
          sheet.replaceSync({{JsonSerializer.Serialize(css)}});
          Object.defineProperty(window, '{{HideKey}}', { value: true });
          const adopt = () => {
            if (!document.adoptedStyleSheets.includes(sheet)) document.adoptedStyleSheets = [...document.adoptedStyleSheets, sheet];
          };
          adopt();
          document.addEventListener('DOMContentLoaded', adopt);
          window.addEventListener('load', adopt);
        })();
        """;

    private static async Task RunQuietlyAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException or COMException)
        {
            // The window closed, or the page went on to another.
        }
    }

    // Lit while it blocks on the page, with how many it has; muted while it's off, paused, or leaving the site alone.
    private void UpdateShield()
    {
        var site = AdBlockerViewModel.SiteOf(_pageHost);
        var showsPage = _pageHost.Length > 0 && StartPage.Visibility != Visibility.Visible;
        string status;
        var lit = false;
        if (!_adBlock.IsEnabled) status = "Off: pages show their ads.";
        else if (!showsPage) status = "On.";
        else if (AdBlockerViewModel.IsLocal(_pageHost)) status = "Not on this PC's own sites, such as your projects' local ones.";
        else if (_adBlock.IsPaused(_pageHost)) status = $"Paused on {site} until the app closes.";
        else if (_adBlock.Engine.AllowsPage(_pageHost)) status = $"Not on {site}: the filter lists leave it alone, as blocking breaks it.";
        else
        {
            lit = true;
            status = _blocked switch
            {
                0 => "Nothing blocked on this page.",
                1 => "1 blocked on this page.",
                _ => $"{_blocked:N0} blocked on this page.",
            };
        }

        ShieldGlyph.Foreground = (Brush)FindResource(lit ? "Accent" : "Text.Muted");
        ShieldCount.Text = _blocked > 999 ? "999+" : _blocked.ToString();
        ShieldCount.Foreground = ShieldGlyph.Foreground;
        ShieldCount.Visibility = lit && _blocked > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShieldButton.ToolTip = $"Ad blocker: {status}";
        AutomationProperties.SetHelpText(ShieldButton, status);

        ShieldStatus.Text = status;
        var canPause = _adBlock.IsEnabled && showsPage && !AdBlockerViewModel.IsLocal(_pageHost) && !_adBlock.Engine.AllowsPage(_pageHost);
        PauseButton.Visibility = canPause ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.Content = _adBlock.IsPaused(_pageHost) ? $"Resume on {site}" : $"Pause on {site}";
        PauseButton.ToolTip = _adBlock.IsPaused(_pageHost)
            ? "Block ads on this site again"
            : "Let this site show its ads until the app closes, in every browser window";
    }

    private void Shield_Click(object sender, RoutedEventArgs e)
    {
        if (Toolbar.Visibility != Visibility.Visible) RevealToolbar();
        UpdateShield();
        ShieldPopup.IsOpen = !ShieldPopup.IsOpen;
    }

    // Turned on or off, or made stronger or weaker, here: the page is shown again as it now blocks.
    private async void ShieldOption_Click(object sender, RoutedEventArgs e) => await ReloadForAdBlockAsync();

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        _adBlock.SetPaused(_pageHost, !_adBlock.IsPaused(_pageHost));
        await ReloadForAdBlockAsync();
    }

    private async Task ReloadForAdBlockAsync()
    {
        // The rules a new strength reads may still be on their way; the page waits for them, and the hiding script it gets.
        await _adBlock.WhenReadyAsync();
        await _hiding;
        if (_closed || CurrentPage() == null) return;
        Web.CoreWebView2?.Reload();
    }

    private void ShieldSettings_Click(object sender, RoutedEventArgs e)
    {
        ShieldPopup.IsOpen = false;
        _sites.OpenSettings();
    }

    /// <summary>Shows the run's site, waiting a while for its server to answer.</summary>
    private void Go(string url)
    {
        _url = url;
        Address.Text = url;
        _waitUntil = DateTime.UtcNow + WaitForServer;
        _retry.Stop();
        // The page shows once the server has answered (or the wait is given up), as OnNavigationCompleted says.
        StartPage.Visibility = Visibility.Collapsed;

        if (_webViewFailed) OpenInDefaultBrowser(url);
        else TryNavigate(url); // until it's ready, StartAsync goes there once it is
    }

    /// <summary>
    /// Shows a site typed in the address or saved: straight away, with the browser's own page if it doesn't answer,
    /// rather than waiting for it as for a run's. False when it isn't an address the browser can go to.
    /// </summary>
    private bool Browse(string url)
    {
        if (_webViewFailed)
        {
            OpenInDefaultBrowser(url);
            return true;
        }
        if (!TryNavigate(url)) return false;

        _url = url;
        _waitUntil = DateTime.MinValue;
        _retry.Stop();
        if (!Address.IsKeyboardFocusWithin) Address.Text = url;
        StartPage.Visibility = Visibility.Collapsed;
        WaitingNote.Visibility = Visibility.Collapsed;
        Web.Visibility = Visibility.Visible;
        UpdateStar();
        return true;
    }

    // Until the browser is ready there's nothing to go there with; StartAsync goes to _url once it is.
    private bool TryNavigate(string url)
    {
        try
        {
            Web.CoreWebView2?.Navigate(url);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private void ShowStartPage()
    {
        _url = string.Empty;
        Address.Text = string.Empty;
        Web.Visibility = Visibility.Hidden;
        StartPage.Visibility = Visibility.Visible;
        UpdateStar();
        UpdateShield();
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
        StartPage.Visibility = Visibility.Collapsed;
        Web.Visibility = Visibility.Visible;
        SetLoading(false);
        UpdateStar();
        UpdateShield();
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
        if (web == null || StartPage.Visibility == Visibility.Visible) return;

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

    // An address goes there (without a scheme, such as localhost:3000, http for this PC's sites and https for others');
    // anything else is searched for, as Settings says.
    private void Address_KeyDown(object sender, KeyEventArgs e)
    {
        var web = Web.CoreWebView2;
        if (e.Key == Key.Escape)
        {
            if (StartPage.Visibility == Visibility.Visible) Address.Text = string.Empty;
            else if (web != null) Address.Text = web.Source;
            if (Web.Visibility == Visibility.Visible) Web.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            var text = Address.Text.Trim();
            if (text.Length == 0) return;
            if (Browse(AddressInput.Resolve(text, _sites.SearchEngine)))
            {
                if (web != null) Web.Focus();
            }
            else
            {
                Address.SelectAll();
            }
            e.Handled = true;
        }
    }

    private void Address_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        Dispatcher.BeginInvoke(() => Address.SelectAll(), DispatcherPriority.Input);

    // ---- Saved sites ----

    private void SavedSite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SavedSite site }) Browse(site.Url);
    }

    private void Star_Click(object sender, RoutedEventArgs e) => SaveOrEditPage();

    // The star, or Ctrl+D: saves the page shown, named after its title, and opens it to rename; on a saved site, just that.
    private void SaveOrEditPage()
    {
        if (CurrentPage() is not { } url) return;
        if (Toolbar.Visibility != Visibility.Visible) RevealToolbar(); // in full screen, for the popup to show under the star

        var saved = _sites.FindSaved(url);
        var site = saved ?? _sites.Save(url, PageName(url));
        EditSite(site, StarButton, saved == null ? "SITE SAVED" : "SAVED SITE");
    }

    // The page shown, when it's a site that can be saved: not the start page, nor the browser's own pages.
    private string? CurrentPage()
    {
        if (_webViewFailed || StartPage.Visibility == Visibility.Visible) return null;
        var source = Web.CoreWebView2?.Source;
        return source != null && (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                                  || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            ? source
            : null;
    }

    private string PageName(string url)
    {
        var title = Web.CoreWebView2?.DocumentTitle?.Trim();
        if (!string.IsNullOrEmpty(title) && title != url) return title;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Authority : url;
    }

    // Lit while the page shown is saved, so the star says whether it is.
    private void UpdateStar()
    {
        var url = CurrentPage();
        var saved = _sites.FindSaved(url) != null;
        StarButton.IsEnabled = url != null;
        StarButton.Content = saved ? "" : "";
        if (saved) StarButton.Foreground = (Brush)FindResource("Accent");
        else StarButton.ClearValue(ForegroundProperty);
        StarButton.ToolTip = saved ? "Edit this saved site (Ctrl+D)" : "Save this site (Ctrl+D)";
        AutomationProperties.SetName(StarButton, saved ? "Edit this saved site" : "Save this site");
    }

    // Saved or removed in this window or another.
    private void OnSavedSitesChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateStar();

    // The popup to rename a saved site or change its address, under the star or the saved site picked.
    private void EditSite(SavedSite site, FrameworkElement anchor, string heading)
    {
        if (SitePopup.IsOpen) SitePopup.IsOpen = false; // keeps what was typed for the one it was editing

        _editing = site;
        _keepEdits = true;
        SitePopupHeading.Text = heading;
        SiteName.Text = site.Name;
        SiteAddress.Text = site.Url;
        SiteAddressError.Visibility = Visibility.Collapsed;
        SitePopup.PlacementTarget = anchor;
        _popupAlignRight = anchor == StarButton;
        SitePopup.IsOpen = true;
    }

    private void SitePopup_Opened(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        SiteName.Focus();
        SiteName.SelectAll();
    }, DispatcherPriority.Input);

    // Clicking away keeps what was typed, as Done does; an address that isn't one keeps the one it had.
    private void SitePopup_Closed(object? sender, EventArgs e)
    {
        if (_keepEdits) ApplyEdits(showError: false);
        _editing = null;
        _keepEdits = false;
        UpdateStar();
    }

    private void SiteEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            FinishEditing();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _keepEdits = false;
            SitePopup.IsOpen = false;
            e.Handled = true;
        }
    }

    private void DoneSite_Click(object sender, RoutedEventArgs e) => FinishEditing();

    private void FinishEditing()
    {
        if (!ApplyEdits(showError: true)) return;
        _keepEdits = false;
        SitePopup.IsOpen = false;
    }

    private void RemoveSite_Click(object sender, RoutedEventArgs e)
    {
        _keepEdits = false;
        if (_editing != null) _sites.Remove(_editing);
        SitePopup.IsOpen = false;
    }

    private bool ApplyEdits(bool showError)
    {
        if (_editing == null) return true;

        var url = SiteAddressFrom(SiteAddress.Text);
        if (url == null)
        {
            if (showError)
            {
                SiteAddressError.Text = "Type a web address, such as localhost:3000 or https://example.com.";
                SiteAddressError.Visibility = Visibility.Visible;
                SiteAddress.Focus();
                SiteAddress.SelectAll();
                return false;
            }
            url = _editing.Url;
        }

        var name = SiteName.Text.Trim();
        if (name.Length == 0) name = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Authority : url;
        _editing = _sites.Update(_editing, name, url);
        return true;
    }

    // As the address takes it: without a scheme, such as localhost:3000, it's http. Null when it isn't a web address.
    private static string? SiteAddressFrom(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        if (!text.Contains("://")) text = "http://" + text;
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
               && uri.Host.Length > 0
            ? text
            : null;
    }

    // A saved site's menu, in the bar or on the start page: the site is the DataContext of what it was opened on.
    private static (SavedSite? Site, FrameworkElement? Target) MenuSite(object sender) =>
        (sender as MenuItem)?.Parent is ContextMenu { PlacementTarget: FrameworkElement target }
            ? (target.DataContext as SavedSite, target)
            : (null, null);

    private void SiteMenuOpenInDefaultBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (MenuSite(sender).Site is { } site) OpenInDefaultBrowser(site.Url);
    }

    private void SiteMenuEdit_Click(object sender, RoutedEventArgs e)
    {
        if (MenuSite(sender) is ({ } site, { } target)) EditSite(site, target, "SAVED SITE");
    }

    private void SiteMenuRemove_Click(object sender, RoutedEventArgs e)
    {
        if (MenuSite(sender).Site is { } site) _sites.Remove(site);
    }

    // The page has the keyboard most of the time; it passes these keys on, as it does F5, F12 and Alt+arrows to Edge.
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.F11)
        {
            SetFullScreen(!_isFullScreen);
            e.Handled = true;
        }
        else if (key == Key.Escape && _isFullScreen && !SitePopup.IsOpen && !ShieldPopup.IsOpen
                 && Web.CoreWebView2?.ContainsFullScreenElement != true)
        {
            SetFullScreen(false);
            e.Handled = true;
        }
        else if ((key == Key.L && Keyboard.Modifiers == ModifierKeys.Control) || (key == Key.D && Keyboard.Modifiers == ModifierKeys.Alt))
        {
            Address.Focus();
            e.Handled = true;
        }
        else if (key == Key.D && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SaveOrEditPage();
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

    private void RevealStrip_MouseEnter(object sender, MouseEventArgs e) => RevealToolbar();

    private void RevealToolbar()
    {
        Toolbar.Visibility = Visibility.Visible;
        RevealStrip.Visibility = Visibility.Collapsed;
    }

    private void Toolbar_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_isFullScreen || Address.IsKeyboardFocusWithin || SitePopup.IsOpen || ShieldPopup.IsOpen) return;
        Toolbar.Visibility = Visibility.Collapsed;
        RevealStrip.Visibility = Visibility.Visible;
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _retry.Stop();
        _sites.SavedSites.CollectionChanged -= OnSavedSitesChanged;
        _adBlock.Changed -= OnAdBlockChanged;
        Web.Dispose();
        base.OnClosed(e);
    }
}
