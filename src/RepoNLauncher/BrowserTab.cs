using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RepoNLauncher.Services;
using RepoNLauncher.ViewModels;

namespace RepoNLauncher;

/// <summary>
/// A tab in a dashboard browser window: its page, in a browser of its own that goes with it when it's dragged to another
/// window, what shows instead while it waits for a run's server or before an address is typed, and what the ad blocker
/// has blocked on its page. A project's run has a tab of its own, which running it again goes back to.
/// </summary>
public sealed partial class BrowserTab : ObservableObject, IDisposable
{
    // Edge's data for the sites (their cookies and storage, so a sign-in lasts), beside the file preview's but apart from it.
    private static readonly string WebViewData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Path.GetFileName(DashboardConfigService.AppDataDir), "Browser");

    // A dev server can print its address a moment before it answers. Until it does, the tab tries again every second
    // for this long, rather than showing the browser's error page.
    private static readonly TimeSpan WaitForServer = TimeSpan.FromSeconds(30);

    // Names what the hiding script leaves on the page, so pages can't tell it by name (it's new each run).
    private static readonly string HideKey = "_" + Guid.NewGuid().ToString("N")[..10];

    private readonly AdBlockerViewModel _adBlock;
    private readonly DispatcherTimer _retry = new() { Interval = TimeSpan.FromSeconds(1) };

    // The site the run serves (or the one last typed or saved), and until when a failure to reach it means it's still
    // starting. Empty while the tab shows its start page.
    private string _url = string.Empty;
    private DateTime _waitUntil;

    // Whether the page shows (rather than the start page or the note), while the tab is the one its window shows.
    private bool _webShown;
    private bool _closed;

    // Whether the ad blocker blocks on the page, and whether requests are handed to the tab to check (only while it's on).
    private bool _filterPage;
    private bool _checkingRequests;

    // Hiding the page's ads: the CSS a script added for each new page puts on it, that script once it's added, and whether
    // the page started loading before it was, so it's run on the page once that has loaded.
    private string? _hideCss;
    private string? _hideScriptId;
    private bool _hideLate;
    private Task _hiding = Task.CompletedTask;

    // Windows of their own the page's browser opened for it: its developer tools, or a pop-up the page sized itself.
    private readonly List<IntPtr> _popups = new();

    public BrowserTab(AdBlockerViewModel adBlock, bool incognito, string? projectPath = null, string? projectName = null)
    {
        _adBlock = adBlock;
        Incognito = incognito;
        ProjectPath = projectPath;
        ProjectName = projectName;
        _showsStartPage = true;
        Web.Visibility = Visibility.Collapsed;
        _adBlock.Changed += OnAdBlockChanged;
        _retry.Tick += (_, _) =>
        {
            _retry.Stop();
            TryNavigate(_url);
        };
    }

    /// <summary>The tab's page. Only its window shows it, and only while it's that window's tab picked.</summary>
    public WebView2 Web { get; } = new();

    /// <summary>The project whose run this tab shows, or null for a tab of no project's.</summary>
    public string? ProjectPath { get; }
    public string? ProjectName { get; }

    /// <summary>Keeps nothing on this PC; it can only go to windows that don't either.</summary>
    public bool Incognito { get; }

    /// <summary>The window the tab is in now.</summary>
    internal BrowserWindow? Window { get; set; }

    /// <summary>The site it shows, or was last asked to; empty on the start page.</summary>
    public string Url => _url;

    /// <summary>The browser couldn't be started here; sites open in the default browser instead.</summary>
    public bool Failed { get; private set; }

    /// <summary>The page's site, for the ad blocker; empty until a page is gone to.</summary>
    public string PageHost { get; private set; } = string.Empty;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Title))] private string? _pageTitle;

    /// <summary>What the address shows for this tab: the page's address, or the one it's going to.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Title))] private string _address = string.Empty;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Title))] private bool _showsStartPage;

    /// <summary>Shown instead of the page, such as while the run's server doesn't answer yet; null when there's none.</summary>
    [ObservableProperty] private string? _note;

    [ObservableProperty] private ImageSource? _favicon;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _canGoBack;
    [ObservableProperty] private bool _canGoForward;

    /// <summary>The tab its window shows.</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>How many requests the ad blocker has blocked on the page.</summary>
    [ObservableProperty] private int _blocked;

    /// <summary>The tab's name: the page's title, else the project's name or the site's.</summary>
    public string Title =>
        !string.IsNullOrWhiteSpace(PageTitle) ? PageTitle!
        : ShowsStartPage ? ProjectName ?? "New tab"
        : ProjectName ?? (Uri.TryCreate(Address, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host
            : Address.Length > 0 ? Address : "New tab");

    /// <summary>The ad blocker's view of the page changed (it was turned on or off, say), which the shield shows.</summary>
    public event Action<BrowserTab>? PageChanged;

    /// <summary>The page went full screen, such as a video's, or left it.</summary>
    public event Action<BrowserTab, bool>? FullScreenRequested;

    /// <summary>The page opens another, such as a link with target="_blank", which the window opens in a tab.</summary>
    public event Action<BrowserTab, CoreWebView2NewWindowRequestedEventArgs>? NewTabRequested;

    /// <summary>The page closed itself (window.close()), as one it opened may.</summary>
    public event Action<BrowserTab>? CloseRequested;

    partial void OnIsSelectedChanged(bool value) => UpdateWebVisibility();

    private void UpdateWebVisibility() =>
        Web.Visibility = !IsSelected ? Visibility.Collapsed : _webShown ? Visibility.Visible : Visibility.Hidden;

    private void SetWebShown(bool shown)
    {
        _webShown = shown;
        UpdateWebVisibility();
    }

    // Every tab's environment is made the same way, as Edge needs for browsers sharing a data folder.
    internal static Task<CoreWebView2Environment> CreateEnvironmentAsync() => CoreWebView2Environment.CreateAsync(null, WebViewData);

    /// <summary>Starts the tab's browser, and goes to the site it was given.</summary>
    public async Task OpenAsync()
    {
        if (await InitAsync()) await StartAsync();
    }

    /// <summary>
    /// Makes the tab's browser, in <paramref name="environment"/> (an opener's, for a page it opens) or one of its own.
    /// False when it couldn't be made, or the tab was closed meanwhile.
    /// </summary>
    public async Task<bool> InitAsync(CoreWebView2Environment? environment = null)
    {
        // Edge takes a page to belong to the window its browser is made in, and closes it with that window, even after
        // the tab has been dragged to another. So it's made in a window that lasts as long as the app, and then goes to
        // the tab's window (the one it's in by then: it may have been dragged meanwhile).
        Nursery.Add(Web);
        try
        {
            // Incognito keeps the tab's browsing data in memory, so none of it reaches the data folder.
            environment ??= await CreateEnvironmentAsync();
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.IsInPrivateModeEnabled = Incognito;
            await Web.EnsureCoreWebView2Async(environment, options);
        }
        catch (Exception e) when (e is WebView2RuntimeNotFoundException or ExternalException or IOException or UnauthorizedAccessException
                                      or ArgumentException or InvalidOperationException)
        {
            Nursery.Remove(Web);
            // Closed while it was starting, or no Edge to show it with: the default browser shows the site instead.
            if (_closed) return false;
            Failed = true;
            ShowsStartPage = false;
            SetWebShown(false);
            var opened = _url.Length > 0 ? "the site opened" : "sites open";
            Note = e is WebView2RuntimeNotFoundException
                ? $"This window needs Microsoft Edge WebView2, which this PC doesn't have, so {opened} in your default browser."
                : $"Sites couldn't be shown here ({e.Message.TrimEnd('.')}), so {opened} in your default browser.";
            OpenInDefaultBrowser(_url);
            return false;
        }

        Nursery.Remove(Web);
        if (_closed) return false;
        Window?.ShowPage(this);
        var web = Web.CoreWebView2;
        web.DocumentTitleChanged += (_, _) => PageTitle = web.DocumentTitle;
        web.SourceChanged += (_, _) => Address = web.Source;
        web.HistoryChanged += (_, _) =>
        {
            CanGoBack = web.CanGoBack;
            CanGoForward = web.CanGoForward;
        };
        web.NavigationStarting += OnNavigationStarting;
        web.NavigationCompleted += OnNavigationCompleted;
        web.WebResourceRequested += OnWebResourceRequested;
        web.NewWindowRequested += OnNewWindowRequested;
        web.WindowCloseRequested += (_, _) => CloseRequested?.Invoke(this);
        web.FaviconChanged += async (_, _) =>
        {
            var icon = await FaviconOfAsync(web);
            if (!_closed) Favicon = icon;
        };
        web.ContentLoading += (_, _) => _hideLate = _hideCss != null && _hideScriptId == null;
        web.DOMContentLoaded += (_, _) =>
        {
            if (_hideLate && _hideCss != null) _ = RunQuietlyAsync(web.ExecuteScriptAsync(HideScript(_hideCss)));
            _hideLate = false;
        };
        // The page's own full screen, such as a video's, takes the window's with it.
        web.ContainsFullScreenElementChanged += (_, _) => FullScreenRequested?.Invoke(this, web.ContainsFullScreenElement);
        // A dev server's HTTPS certificate is its own, which Edge doesn't trust unless it was set up to (ASP.NET's dev-certs,
        // say). Like Chrome's allow-insecure-localhost, that's let through for this PC's own servers only.
        web.ServerCertificateErrorDetected += (_, e) =>
        {
            if (Uri.TryCreate(e.RequestUri, UriKind.Absolute, out var uri) && uri.IsLoopback)
                e.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
        };
        BrowserWindow.WatchBrowserProcess(web.BrowserProcessId);
        return true;
    }

    /// <summary>
    /// Once its browser is made: the ad blocker's rules (read once for every tab, on another thread, so the first page is
    /// filtered too), then the site it was given, or one typed or picked while it was starting.
    /// </summary>
    public async Task StartAsync(bool navigate = true)
    {
        await _adBlock.WhenReadyAsync();
        if (_closed) return;
        UpdateRequestChecking();
        if (navigate && _url.Length > 0 && !TryNavigate(_url)) ShowStartPage();
    }

    /// <summary>For a page another opens in it: the page shows straight away, with the address it was opened at.</summary>
    public void ShowOpened(string url)
    {
        _url = url;
        Address = url;
        ShowsStartPage = false;
        SetWebShown(true);
    }

    // ---- Going to sites ----

    /// <summary>Shows the run's site, waiting a while for its server to answer.</summary>
    public void Go(string url)
    {
        _url = url;
        Address = url;
        _waitUntil = DateTime.UtcNow + WaitForServer;
        _retry.Stop();
        // From the start page the page shows as it loads; while a wait's note shows, once the server has answered (or
        // the wait is given up), as OnNavigationCompleted says.
        if (ShowsStartPage) SetWebShown(true);
        ShowsStartPage = false;

        if (Failed) OpenInDefaultBrowser(url);
        else TryNavigate(url); // until it's ready, StartAsync goes there once it is
    }

    /// <summary>
    /// Shows a site typed in the address or saved: straight away, with the browser's own page if it doesn't answer,
    /// rather than waiting for it as for a run's. False when it isn't an address the browser can go to.
    /// </summary>
    public bool Browse(string url)
    {
        if (Failed)
        {
            OpenInDefaultBrowser(url);
            return true;
        }
        if (!TryNavigate(url)) return false;

        _url = url;
        _waitUntil = DateTime.MinValue;
        _retry.Stop();
        Address = url;
        ShowsStartPage = false;
        Note = null;
        SetWebShown(true);
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

    public void ShowStartPage()
    {
        _url = string.Empty;
        Address = string.Empty;
        PageTitle = null;
        Favicon = null;
        SetWebShown(false);
        ShowsStartPage = true;
        PageChanged?.Invoke(this);
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        // While the server starts, a failure is most likely it not answering yet; Edge reports a refused connection as
        // Unknown, not CannotConnect. A cancelled one is a stop, or another page asked for.
        if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled && DateTime.UtcNow < _waitUntil)
        {
            SetWebShown(false);
            Note = $"Waiting for {_url} to answer…";
            _retry.Start();
            IsLoading = true;
            return;
        }

        // Once it has answered (or given up waiting), a failure shows as it is: the server stopped, say.
        _waitUntil = DateTime.MinValue;
        Note = null;
        ShowsStartPage = false;
        SetWebShown(true);
        IsLoading = false;
        PageChanged?.Invoke(this);
    }

    /// <summary>Reload; while a page loads, or the tab waits for the server, Stop.</summary>
    public void ReloadOrStop()
    {
        var web = Web.CoreWebView2;
        if (web == null || ShowsStartPage) return;

        if (!IsLoading)
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
            Note = null;
            SetWebShown(true);
            IsLoading = false;
            web.Navigate(_url);
        }
    }

    public void GoBack() => Web.CoreWebView2?.GoBack();

    public void GoForward() => Web.CoreWebView2?.GoForward();

    public void OpenDevTools() => Web.CoreWebView2?.OpenDevToolsWindow();

    /// <summary>The page shown, when it's a site that can be saved: not the start page, nor the browser's own pages.</summary>
    public string? CurrentPage()
    {
        if (Failed || ShowsStartPage) return null;
        var source = Web.CoreWebView2?.Source;
        return source != null && (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                                  || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            ? source
            : null;
    }

    /// <summary>What to save the page as: its title, else its site.</summary>
    public string PageName(string url)
    {
        var title = Web.CoreWebView2?.DocumentTitle?.Trim();
        if (!string.IsNullOrEmpty(title) && title != url) return title;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Authority : url;
    }

    internal static void OpenInDefaultBrowser(string url)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    // The site's icon, as the page names it, for the tab; none when it has none, or it can't be read.
    private static async Task<ImageSource?> FaviconOfAsync(CoreWebView2 web)
    {
        if (string.IsNullOrEmpty(web.FaviconUri)) return null;
        try
        {
            await using var stream = await web.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            if (stream == null || stream.Length == 0) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 32;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or NotSupportedException or IOException
                                      or ArgumentException)
        {
            return null;
        }
    }

    // ---- Ad blocker ----

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        IsLoading = true;
        // A redirect is still the page asked for, so what was blocked on the way counts towards it.
        if (!e.IsRedirected) Blocked = 0;
        PageHost = Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : string.Empty;
        UpdatePage();
    }

    // The page's site changed, or the ad blocker did: whether it blocks on the page, what it hides there, and the shield.
    private void UpdatePage()
    {
        _filterPage = _adBlock.Filters(PageHost);
        _hiding = UpdateHidingAsync();
        PageChanged?.Invoke(this);
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
        if (context == CoreWebView2WebResourceContext.Document && host == PageHost) return;
        if (!_adBlock.Engine.ShouldBlock(url, host, PageHost, TypeOf(context))) return;

        e.Response = Web.CoreWebView2.Environment.CreateWebResourceResponse(null, 403, "Blocked by the ad blocker", string.Empty);
        Blocked++;
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

    // Pop-ups the lists know as ads, and on Strict any the page opens without a click, are blocked. Others open in a
    // tab, but for a pop-up the page sized or placed itself (a sign-in's, say), which keeps a window of its own.
    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (_filterPage)
        {
            var unasked = _adBlock.Level == AdBlockLevel.Strict && !e.IsUserInitiated;
            var listed = Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)
                         && _adBlock.Engine.ShouldBlockPopup(e.Uri, uri.Host.ToLowerInvariant(), PageHost);
            if (unasked || listed)
            {
                e.Handled = true;
                Blocked++;
                return;
            }
        }

        var features = e.WindowFeatures;
        if (features.HasSize || features.HasPosition) return;
        NewTabRequested?.Invoke(this, e);
    }

    // The CSS for the page's site goes in a script run as each page starts, before its own scripts; it's added again
    // only when the CSS changes, as the site does. Added after the page has started, it's run once that has loaded.
    private async Task UpdateHidingAsync()
    {
        var web = Web.CoreWebView2;
        if (web == null) return;
        var css = _filterPage ? _adBlock.Engine.HidingCss(PageHost) : null;
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
            // The tab closed, or the page went on to another.
        }
    }

    /// <summary>Shows the page again as the ad blocker now blocks, once its rules, and the hiding script, are ready.</summary>
    public async Task ReloadForAdBlockAsync()
    {
        await _adBlock.WhenReadyAsync();
        await _hiding;
        if (_closed || CurrentPage() == null) return;
        Web.CoreWebView2?.Reload();
    }

    // ---- Where its browser is made ----

    /// <summary>
    /// A window of the app's own, never shown, that lasts as long as the app: every tab's browser is made in it, then
    /// goes to the tab's window, so closing a browser window never takes a tab dragged out of it with it.
    /// </summary>
    private static class Nursery
    {
        private static Grid? _host;

        public static void Add(UIElement web)
        {
            if (_host == null)
            {
                _host = new Grid();
                // A pop-up without WS_VISIBLE: hidden, and off the taskbar and Alt+Tab even so.
                const int WS_POPUP = unchecked((int)0x80000000), WS_EX_TOOLWINDOW = 0x80;
                var parameters = new HwndSourceParameters("Repo'N'Launcher browsers")
                {
                    WindowStyle = WS_POPUP,
                    ExtendedWindowStyle = WS_EX_TOOLWINDOW,
                    Width = 0,
                    Height = 0,
                };
                _ = new HwndSource(parameters) { RootVisual = _host };
            }
            if (VisualTreeHelper.GetParent(web) == null) _host.Children.Add(web);
        }

        public static void Remove(UIElement web) => _host?.Children.Remove(web);
    }

    // ---- Its windows of their own ----

    /// <summary>A window the page's browser opened for this tab, such as its developer tools.</summary>
    internal void AddPopup(IntPtr hwnd)
    {
        _popups.RemoveAll(h => !NativeMethods.IsWindow(h));
        if (!_popups.Contains(hwnd)) _popups.Add(hwnd);
    }

    /// <summary>
    /// Keeps the tab's own windows (its developer tools, say) over its browser window while that's pinned on top, where
    /// they'd otherwise open behind it; unpinned, they're ordinary windows again.
    /// </summary>
    internal void KeepPopupsOnTop(bool onTop)
    {
        _popups.RemoveAll(h => !NativeMethods.IsWindow(h));
        foreach (var hwnd in _popups) NativeMethods.SetTopmost(hwnd, onTop);
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _retry.Stop();
        _adBlock.Changed -= OnAdBlockChanged;
        Nursery.Remove(Web);
        Web.Dispose();
    }
}
