using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using RepoNLauncher.Controls;
using RepoNLauncher.Models;
using RepoNLauncher.Services;
using RepoNLauncher.ViewModels;
using Application = System.Windows.Application;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace RepoNLauncher;

/// <summary>
/// The dashboard's own browser, in tabs as browsers have them: the site a project's run serves has a tab of its own
/// (running it again shows the new run's site there), opened in a window filling a screen of its own, another than the
/// dashboard's when there is one, or the screen picked in the Run tab. Tabs move along the tabs, out to a window of their
/// own, and onto another browser window's tabs by dragging, as in Chrome. Ctrl+N opens another window. A tab opened
/// without a site starts on a page of the saved sites, which every window also shows in a bar under its address.
/// </summary>
public partial class BrowserWindow : Window
{
    // Every browser window open, the one last active first.
    private static readonly List<BrowserWindow> OpenWindows = new();

    // Each project's run tab, wherever it's been dragged to.
    private static readonly Dictionary<string, BrowserTab> ProjectTabs = new(StringComparer.OrdinalIgnoreCase);

    // The sites of tabs closed, the latest last, for Ctrl+Shift+T to open again.
    private static readonly List<(string Url, bool Incognito)> ClosedTabs = new();

    private readonly SiteBrowserViewModel _sites;
    private readonly AdBlockerViewModel _adBlock;

    // Settings' Incognito as the window opened: its tabs keep nothing on this PC. Their browsers can't change that once made.
    private readonly bool _incognito;

    // The tabs in their order along the strip, each one's tab there, and the one shown.
    private readonly List<BrowserTab> _tabs = new();
    private readonly Dictionary<BrowserTab, BrowserTabHeader> _headers = new();
    private BrowserTab? _tab;

    private bool _closed;
    private bool _isFullScreen;
    private WindowState _stateBeforeFullScreen = WindowState.Normal;
    private bool _maximizeOnceMade;

    // The saved site the popup edits, and whether closing it keeps what was typed (not after Remove, or Esc).
    private SavedSite? _editing;
    private bool _keepEdits;

    // Where the popup lines up with what it opened under: its right edge (the star, at the window's right) or its left.
    private bool _popupAlignRight;

    private BrowserWindow(SiteBrowserViewModel sites, bool incognito)
    {
        InitializeComponent();
        _sites = sites;
        _adBlock = sites.AdBlocker;
        _incognito = incognito;
        DataContext = sites;
        Title = "Repo'N'Launcher";
        IncognitoBadge.Visibility = _incognito ? Visibility.Visible : Visibility.Collapsed;
        _sites.SavedSites.CollectionChanged += OnSavedSitesChanged;

        // Placed by hand: Bottom placement follows the pen-tablet "right-handed" setting, which flips popups to the left.
        // The callback works in device pixels.
        SitePopup.CustomPopupPlacementCallback = (popupSize, targetSize, _) => PlaceUnder(popupSize, targetSize, _popupAlignRight);
        ShieldPopup.CustomPopupPlacementCallback = (popupSize, targetSize, _) => PlaceUnder(popupSize, targetSize, alignRight: true);
        UpdateShield();

        SourceInitialized += (_, _) =>
        {
            NativeMethods.UseThemedTitleBar(new WindowInteropHelper(this).Handle);
            if (_maximizeOnceMade) WindowState = WindowState.Maximized;
        };
        Loaded += (_, _) =>
        {
            NextScreenButton.Visibility = Screen.AllScreens.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
            if (_tab?.ShowsStartPage == true) Address.Focus();
        };
        Activated += (_, _) =>
        {
            OpenWindows.Remove(this);
            OpenWindows.Insert(0, this);
        };
        OpenWindows.Insert(0, this);
    }

    /// <summary>
    /// Shows the site in the project's tab (or a browser window of no project's), opening a window for it on the screen
    /// asked for if it has none; without a site, it just opens, on its start page.
    /// </summary>
    public static void Open(SiteBrowserViewModel sites, SiteRequest request)
    {
        if (request.ProjectPath is { } path && ProjectTabs.TryGetValue(path, out var tab) && tab.Window is { } window)
        {
            if (request.Url != null) tab.Go(request.Url);
            window.Select(tab);
            ShowWindow(window, request.Activate);
            return;
        }

        // The header's Browser: the browser window last used that has no project's run in it.
        if (request.ProjectPath == null && OpenWindows.FirstOrDefault(w => w._tabs.All(t => t.ProjectPath == null)) is { } browser)
        {
            if (request.Url != null) browser.NewTab(request.Url, waitForServer: true);
            ShowWindow(browser, request.Activate);
            return;
        }

        var created = new BrowserWindow(sites, sites.IsIncognito);
        created.NewTab(request.Url, waitForServer: true, projectPath: request.ProjectPath, projectName: request.ProjectName);
        created.PlaceOn(TargetScreen(request.Screen));
        created.Show();
    }

    private static void ShowWindow(BrowserWindow window, bool activate)
    {
        if (activate) NativeMethods.BringToFront(window);
        else if (window.WindowState == WindowState.Minimized) SystemCommands.RestoreWindow(window);
    }

    /// <summary>Moves the open browser windows to the screen picked in the Run tab.</summary>
    public static void MoveAllTo(string? screen)
    {
        foreach (var window in OpenWindows.ToList()) window.PlaceOn(TargetScreen(screen));
    }

    /// <summary>Closes every browser window; they aren't the dashboard's own, so they'd keep the app open.</summary>
    public static void CloseAll()
    {
        EndDrag();
        foreach (var window in OpenWindows.ToList()) window.Close();
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

    /// <summary>
    /// Another browser window, over this one a little down and to the right as browsers open one, the same size and
    /// maximized if this one is; with <paramref name="tab"/> (taken from the window it's in) or a new tab's start page.
    /// </summary>
    private BrowserWindow OpenNewWindow(bool incognito, BrowserTab? tab = null)
    {
        var window = new BrowserWindow(_sites, incognito);
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (bounds.IsEmpty) bounds = new Rect(Left, Top, Width, Height);
        window.Width = bounds.Width;
        window.Height = bounds.Height;
        window.Left = bounds.Left + 32;
        window.Top = bounds.Top + 32;
        window._maximizeOnceMade = (_isFullScreen ? _stateBeforeFullScreen : WindowState) == WindowState.Maximized;

        if (tab == null)
        {
            window.NewTab();
        }
        else
        {
            tab.Window?.RemoveTab(tab);
            window.AddTab(tab, 0, select: true);
        }
        window.Show();
        return window;
    }

    private CustomPopupPlacement[] PlaceUnder(System.Windows.Size popupSize, System.Windows.Size targetSize, bool alignRight)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return [new CustomPopupPlacement(
            new Point(alignRight ? targetSize.Width - popupSize.Width : 0, targetSize.Height + 4 * dpi.DpiScaleY),
            PopupPrimaryAxis.Horizontal)];
    }

    private string WindowTitle(BrowserTab tab)
    {
        var label = tab.ProjectName ?? "Browser";
        if (_incognito) label = $"{label} (Incognito)";
        return string.IsNullOrWhiteSpace(tab.PageTitle) ? $"{label} - Repo'N'Launcher" : $"{tab.PageTitle} - {label} - Repo'N'Launcher";
    }

    /// <summary>
    /// Clears what the dashboard browser has kept on this PC: history, cookies and sign-ins, form entries, site storage
    /// and the cache. A hidden browser in <paramref name="host"/> does it, so it works with no browser window open.
    /// </summary>
    public static async Task ClearBrowsingDataAsync(Window host)
    {
        var environment = await BrowserTab.CreateEnvironmentAsync();
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

    // ---- Tabs ----

    /// <summary>
    /// A new tab at <paramref name="index"/> (after the others when it's null): on the site, waiting for its server when
    /// it's a run's, or on its start page.
    /// </summary>
    private BrowserTab NewTab(string? url = null, bool waitForServer = false, int? index = null, bool select = true,
        string? projectPath = null, string? projectName = null)
    {
        var tab = new BrowserTab(_adBlock, _incognito, projectPath, projectName);
        if (projectPath != null) ProjectTabs[projectPath] = tab;
        if (url != null)
        {
            if (waitForServer) tab.Go(url);
            else tab.Browse(url);
        }
        AddTab(tab, index ?? _tabs.Count, select);
        _ = tab.OpenAsync();
        return tab;
    }

    // A tab joins the window: new, or from another.
    private void AddTab(BrowserTab tab, int index, bool select)
    {
        index = Math.Clamp(index, 0, _tabs.Count);
        tab.Window = this;
        _tabs.Insert(index, tab);

        var header = new BrowserTabHeader
        {
            Content = tab,
            DataContext = tab,
            ContentTemplate = (DataTemplate)FindResource("TabHeader"),
            ContextMenu = (ContextMenu)FindResource("TabMenu"),
            Focusable = false,
            RenderTransform = new TranslateTransform(),
        };
        header.SetBinding(AutomationProperties.NameProperty, nameof(BrowserTab.Title));
        header.SetBinding(BrowserTabHeader.IsSelectedProperty, nameof(BrowserTab.IsSelected));
        header.SelectRequested += (_, _) => Select(tab);
        header.PreviewMouseLeftButtonDown += TabHeader_PreviewMouseLeftButtonDown;
        header.PreviewMouseLeftButtonUp += (_, _) => EndDrag();
        header.MouseUp += TabHeader_MouseUp;
        _headers[tab] = header;
        TabStrip.Children.Insert(index, header);
        if (tab.Web.CoreWebView2 != null) ShowPage(tab);

        tab.PropertyChanged += OnTabPropertyChanged;
        tab.PageChanged += OnTabPageChanged;
        tab.FullScreenRequested += OnTabFullScreenRequested;
        tab.NewTabRequested += OnNewTabRequested;
        tab.CloseRequested += CloseTab;
        tab.KeepPopupsOnTop(Topmost);

        if (select || _tab == null) Select(tab);
        else tab.IsSelected = false;
    }

    /// <summary>
    /// Puts the tab's page in the window, once its browser is made (elsewhere, so it outlives this window if the tab is
    /// dragged out of it).
    /// </summary>
    internal void ShowPage(BrowserTab tab)
    {
        if (tab.Window == this && VisualTreeHelper.GetParent(tab.Web) == null) WebHost.Children.Add(tab.Web);
    }

    // Takes the tab off the window, to close it or for it to go to another; the window closes with its last tab.
    private void RemoveTab(BrowserTab tab)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0) return;

        tab.PropertyChanged -= OnTabPropertyChanged;
        tab.PageChanged -= OnTabPageChanged;
        tab.FullScreenRequested -= OnTabFullScreenRequested;
        tab.NewTabRequested -= OnNewTabRequested;
        tab.CloseRequested -= CloseTab;
        _tabs.RemoveAt(index);
        TabStrip.Children.Remove(_headers[tab]);
        _headers.Remove(tab);
        WebHost.Children.Remove(tab.Web);
        tab.Window = null;

        if (_tab == tab)
        {
            tab.IsSelected = false;
            _tab = null;
            // The one that takes its place, as browsers do; the one before it when it was the last.
            if (_tabs.Count > 0) Select(_tabs[Math.Min(index, _tabs.Count - 1)]);
        }
        if (_tabs.Count == 0 && !_closed) Close();
    }

    private void CloseTab(BrowserTab tab)
    {
        if (tab.Window != this) return;
        if (tab.CurrentPage() is { } url)
        {
            ClosedTabs.Add((url, tab.Incognito));
            if (ClosedTabs.Count > 25) ClosedTabs.RemoveAt(0);
        }
        RemoveTab(tab);
        Forget(tab);
        tab.Dispose();
    }

    private static void Forget(BrowserTab tab)
    {
        if (tab.ProjectPath != null && ProjectTabs.TryGetValue(tab.ProjectPath, out var known) && known == tab)
            ProjectTabs.Remove(tab.ProjectPath);
    }

    // Ctrl+Shift+T: the last tab closed of this window's kind (incognito or not), on its site again.
    private void ReopenClosedTab()
    {
        var last = ClosedTabs.FindLastIndex(t => t.Incognito == _incognito);
        if (last < 0) return;
        var url = ClosedTabs[last].Url;
        ClosedTabs.RemoveAt(last);
        NewTab(url);
    }

    private void Select(BrowserTab tab)
    {
        if (_tab == tab) return;
        if (_tab != null) _tab.IsSelected = false;
        _tab = tab;
        tab.IsSelected = true;

        SitePopup.IsOpen = false;
        ShieldPopup.IsOpen = false;
        Address.Text = tab.Address;
        ShowTab();
        // The page has the keyboard, or the address on a start page.
        if (!IsActive) return;
        if (tab.ShowsStartPage) Address.Focus();
        else if (tab.Web.Visibility == Visibility.Visible) tab.Web.Focus();
    }

    // Ctrl+Tab and Ctrl+Shift+Tab: the next tab along, or the one before, round from the ends.
    private void SelectBy(int step)
    {
        if (_tab == null || _tabs.Count < 2) return;
        Select(_tabs[(_tabs.IndexOf(_tab) + step + _tabs.Count) % _tabs.Count]);
    }

    // The toolbar, title and what shows instead of the page, for the tab shown.
    private void ShowTab()
    {
        if (_tab is not { } tab) return;
        Title = WindowTitle(tab);
        BackButton.IsEnabled = tab.CanGoBack;
        ForwardButton.IsEnabled = tab.CanGoForward;
        SetLoading(tab.IsLoading);
        StartPage.Visibility = tab.ShowsStartPage ? Visibility.Visible : Visibility.Collapsed;
        WaitingText.Text = tab.Note ?? string.Empty;
        WaitingNote.Visibility = tab.Note != null ? Visibility.Visible : Visibility.Collapsed;
        UpdateStar();
        UpdateShield();
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not BrowserTab tab || tab != _tab) return;
        if (e.PropertyName == nameof(BrowserTab.Address) && !Address.IsKeyboardFocusWithin) Address.Text = tab.Address;
        ShowTab();
    }

    private void OnTabPageChanged(BrowserTab tab)
    {
        if (tab == _tab) ShowTab();
    }

    private void OnTabFullScreenRequested(BrowserTab tab, bool on)
    {
        if (tab == _tab) SetFullScreen(on);
    }

    // A link that opens another window (target="_blank", or window.open without a size): a tab after the page's, as
    // browsers open it; with Ctrl held it opens behind, and with Shift in a window of its own.
    private async void OnNewTabRequested(BrowserTab opener, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (opener.Web.CoreWebView2?.Environment is not { } environment) return;
        var behind = NativeMethods.IsKeyDown(Key.LeftCtrl) || NativeMethods.IsKeyDown(Key.RightCtrl);
        var inWindow = NativeMethods.IsKeyDown(Key.LeftShift) || NativeMethods.IsKeyDown(Key.RightShift);

        var deferral = e.GetDeferral();
        var tab = new BrowserTab(_adBlock, _incognito);
        try
        {
            tab.ShowOpened(e.Uri);
            if (inWindow) OpenNewWindow(_incognito, tab);
            else AddTab(tab, _tabs.IndexOf(opener) + 1, select: !behind);

            if (await tab.InitAsync(environment))
            {
                e.NewWindow = tab.Web.CoreWebView2;
                await tab.StartAsync(navigate: false);
                return;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            // The page that asked went away meanwhile.
        }
        finally
        {
            deferral.Complete();
        }

        // Its browser couldn't be made, or given the page: the page's own pop-up window opens instead, if it still can.
        tab.Window?.RemoveTab(tab);
        tab.Dispose();
    }

    private void NewTab_Click(object sender, RoutedEventArgs e)
    {
        NewTab();
        Address.Focus();
    }

    // Double-clicking past the tabs opens another, as in Firefox.
    private void TabStrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || e.OriginalSource != TabStrip) return;
        NewTab();
        Address.Focus();
        e.Handled = true;
    }

    private void NewWindow_Click(object sender, RoutedEventArgs e) => OpenNewWindow(_sites.IsIncognito);

    private void TabClose_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BrowserTab tab }) CloseTab(tab);
        e.Handled = true;
    }

    // Pressing a tab shows it, and picks it up to drag; × closes it rather than picking it up.
    private void TabHeader_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not BrowserTabHeader { DataContext: BrowserTab tab } header || IsInButton(e.OriginalSource, header)) return;
        Select(tab);
        BeginDrag(tab, header, e.GetPosition(header));
        e.Handled = true;
    }

    // The middle button closes a tab, as in browsers.
    private void TabHeader_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || sender is not FrameworkElement { DataContext: BrowserTab tab }) return;
        CloseTab(tab);
        e.Handled = true;
    }

    private static bool IsInButton(object source, DependencyObject within)
    {
        for (var element = source as DependencyObject; element != null && element != within;
             element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
        {
            if (element is ButtonBase) return true;
        }
        return false;
    }

    // A tab's menu: the tab is the DataContext of what it was opened on.
    private static BrowserTab? MenuTab(object sender) =>
        (sender as MenuItem)?.Parent is ContextMenu { PlacementTarget: FrameworkElement { DataContext: BrowserTab tab } } ? tab : null;

    private void TabMenuNewTab_Click(object sender, RoutedEventArgs e)
    {
        if (MenuTab(sender) is not { } tab) return;
        NewTab(index: _tabs.IndexOf(tab) + 1);
        Address.Focus();
    }

    private void TabMenuReload_Click(object sender, RoutedEventArgs e) => MenuTab(sender)?.ReloadOrStop();

    private void TabMenuDuplicate_Click(object sender, RoutedEventArgs e)
    {
        if (MenuTab(sender) is not { } tab) return;
        var url = tab.CurrentPage() ?? (tab.Url.Length > 0 ? tab.Url : null);
        NewTab(url, index: _tabs.IndexOf(tab) + 1);
    }

    private void TabMenuMoveToNewWindow_Click(object sender, RoutedEventArgs e)
    {
        if (MenuTab(sender) is { } tab && _tabs.Count > 1) OpenNewWindow(_incognito, tab);
    }

    private void TabMenuClose_Click(object sender, RoutedEventArgs e)
    {
        if (MenuTab(sender) is { } tab) CloseTab(tab);
    }

    private void TabMenuCloseOthers_Click(object sender, RoutedEventArgs e)
    {
        if (MenuTab(sender) is not { } tab) return;
        foreach (var other in _tabs.Where(t => t != tab).ToList()) CloseTab(other);
    }

    private void TabMenuCloseRight_Click(object sender, RoutedEventArgs e)
    {
        if (MenuTab(sender) is not { } tab) return;
        foreach (var other in _tabs.Skip(_tabs.IndexOf(tab) + 1).ToList()) CloseTab(other);
    }

    // ---- Windows of the browser's own ----

    /// <summary>
    /// Watches the tabs' browser for windows of its own it opens: a tab's developer tools (F12, the toolbar's button, or
    /// Inspect), or a pop-up a page sized itself. Such a window isn't the browser window's, so it'd open behind one
    /// pinned on top; it's kept over it instead.
    /// </summary>
    internal static void WatchBrowserProcess(uint processId) => NativeMethods.WatchWindowsShown(processId, OnBrowserWindowShown);

    // The tab it's for is the one shown in the browser window last active, where F12 was pressed or the page clicked.
    private static void OnBrowserWindowShown(IntPtr hwnd)
    {
        if (!NativeMethods.IsAppWindow(hwnd) || OpenWindows.FirstOrDefault() is not { _tab: { } tab } window) return;
        tab.AddPopup(hwnd);
        if (window.Topmost) NativeMethods.SetTopmost(hwnd, true);
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        // Pinned on top, or unpinned: the tabs' developer tools and pop-ups with it.
        if (e.Property == TopmostProperty)
        {
            foreach (var tab in _tabs) tab.KeepPopupsOnTop(Topmost);
        }
    }

    // ---- Ad blocker ----

    // Lit while it blocks on the page, with how many it has; muted while it's off, paused, or leaving the site alone.
    private void UpdateShield()
    {
        var pageHost = _tab?.PageHost ?? string.Empty;
        var blocked = _tab?.Blocked ?? 0;
        var site = AdBlockerViewModel.SiteOf(pageHost);
        var showsPage = pageHost.Length > 0 && _tab?.ShowsStartPage == false;
        string status;
        var lit = false;
        if (!_adBlock.IsEnabled) status = "Off: pages show their ads.";
        else if (!showsPage) status = "On.";
        else if (AdBlockerViewModel.IsLocal(pageHost)) status = "Not on this PC's own sites, such as your projects' local ones.";
        else if (_adBlock.IsPaused(pageHost)) status = $"Paused on {site} until the app closes.";
        else if (_adBlock.Engine.AllowsPage(pageHost)) status = $"Not on {site}: the filter lists leave it alone, as blocking breaks it.";
        else
        {
            lit = true;
            status = blocked switch
            {
                0 => "Nothing blocked on this page.",
                1 => "1 blocked on this page.",
                _ => $"{blocked:N0} blocked on this page.",
            };
        }

        // References, not the brushes, so they follow a change of theme
        ShieldGlyph.SetResourceReference(ForegroundProperty, lit ? "Accent" : "Text.Muted");
        ShieldCount.Text = blocked > 999 ? "999+" : blocked.ToString();
        ShieldCount.SetResourceReference(ForegroundProperty, lit ? "Accent" : "Text.Muted");
        ShieldCount.Visibility = lit && blocked > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShieldButton.ToolTip = $"Ad blocker: {status}";
        AutomationProperties.SetHelpText(ShieldButton, status);

        ShieldStatus.Text = status;
        var canPause = _adBlock.IsEnabled && showsPage && !AdBlockerViewModel.IsLocal(pageHost) && !_adBlock.Engine.AllowsPage(pageHost);
        PauseButton.Visibility = canPause ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.Content = _adBlock.IsPaused(pageHost) ? $"Resume on {site}" : $"Pause on {site}";
        PauseButton.ToolTip = _adBlock.IsPaused(pageHost)
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
    private async void ShieldOption_Click(object sender, RoutedEventArgs e)
    {
        if (_tab != null) await _tab.ReloadForAdBlockAsync();
    }

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_tab is not { } tab) return;
        _adBlock.SetPaused(tab.PageHost, !_adBlock.IsPaused(tab.PageHost));
        await tab.ReloadForAdBlockAsync();
    }

    private void ShieldSettings_Click(object sender, RoutedEventArgs e)
    {
        ShieldPopup.IsOpen = false;
        _sites.OpenSettings();
    }

    // ---- Toolbar ----

    // While a page loads, or the tab waits for the server, Reload is Stop.
    private void SetLoading(bool loading)
    {
        ReloadButton.Content = loading ? "" : "";
        ReloadButton.ToolTip = loading ? "Stop" : "Reload (F5)";
        AutomationProperties.SetName(ReloadButton, loading ? "Stop" : "Reload");
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => _tab?.ReloadOrStop();

    private void Back_Click(object sender, RoutedEventArgs e) => _tab?.GoBack();

    private void Forward_Click(object sender, RoutedEventArgs e) => _tab?.GoForward();

    private void DevTools_Click(object sender, RoutedEventArgs e) => _tab?.OpenDevTools();

    private void OpenInDefaultBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (_tab != null) BrowserTab.OpenInDefaultBrowser(_tab.Web.CoreWebView2?.Source ?? _tab.Url);
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
        if (_tab is not { } tab) return;
        if (e.Key == Key.Escape)
        {
            if (tab.ShowsStartPage) Address.Text = string.Empty;
            else if (tab.Web.CoreWebView2 is { } web) Address.Text = web.Source;
            if (tab.Web.Visibility == Visibility.Visible) tab.Web.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            var text = Address.Text.Trim();
            if (text.Length == 0) return;
            if (tab.Browse(AddressInput.Resolve(text, _sites.SearchEngine)))
            {
                if (tab.Web.CoreWebView2 != null) tab.Web.Focus();
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
        if (sender is FrameworkElement { DataContext: SavedSite site }) _tab?.Browse(site.Url);
    }

    private void Star_Click(object sender, RoutedEventArgs e) => SaveOrEditPage();

    // The star, or Ctrl+D: saves the page shown, named after its title, and opens it to rename; on a saved site, just that.
    private void SaveOrEditPage()
    {
        if (_tab?.CurrentPage() is not { } url) return;
        if (Toolbar.Visibility != Visibility.Visible) RevealToolbar(); // in full screen, for the popup to show under the star

        var saved = _sites.FindSaved(url);
        var site = saved ?? _sites.Save(url, _tab.PageName(url));
        EditSite(site, StarButton, saved == null ? "SITE SAVED" : "SAVED SITE");
    }

    // Lit while the page shown is saved, so the star says whether it is.
    private void UpdateStar()
    {
        var url = _tab?.CurrentPage();
        var saved = _sites.FindSaved(url) != null;
        StarButton.IsEnabled = url != null;
        StarButton.Content = saved ? "" : "";
        if (saved) StarButton.SetResourceReference(ForegroundProperty, "Accent");
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
        if (MenuSite(sender).Site is { } site) BrowserTab.OpenInDefaultBrowser(site.Url);
    }

    private void SiteMenuEdit_Click(object sender, RoutedEventArgs e)
    {
        if (MenuSite(sender) is ({ } site, { } target)) EditSite(site, target, "SAVED SITE");
    }

    private void SiteMenuRemove_Click(object sender, RoutedEventArgs e)
    {
        if (MenuSite(sender).Site is { } site) _sites.Remove(site);
    }

    // ---- Keys and full screen ----

    // The page has the keyboard most of the time; it passes these keys on, as it does F5, F12 and Alt+arrows to Edge.
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        var ctrl = modifiers == ModifierKeys.Control;
        var ctrlShift = modifiers == (ModifierKeys.Control | ModifierKeys.Shift);
        if (key == Key.F11)
        {
            SetFullScreen(!_isFullScreen);
        }
        else if (key == Key.Escape && _isFullScreen && !SitePopup.IsOpen && !ShieldPopup.IsOpen
                 && _tab?.Web.CoreWebView2?.ContainsFullScreenElement != true)
        {
            SetFullScreen(false);
        }
        else if ((key == Key.L && ctrl) || (key == Key.D && modifiers == ModifierKeys.Alt))
        {
            Address.Focus();
        }
        else if (key == Key.D && ctrl)
        {
            SaveOrEditPage();
        }
        else if (key == Key.T && ctrl)
        {
            NewTab();
            Address.Focus();
        }
        else if (key == Key.T && ctrlShift)
        {
            ReopenClosedTab();
        }
        else if (key == Key.N && (ctrl || ctrlShift))
        {
            OpenNewWindow(ctrlShift || _sites.IsIncognito);
        }
        else if ((key == Key.W || key == Key.F4) && ctrl)
        {
            if (_tab != null) CloseTab(_tab);
        }
        else if ((key == Key.Tab && ctrl) || (key == Key.PageDown && ctrl))
        {
            SelectBy(1);
        }
        else if ((key == Key.Tab && ctrlShift) || (key == Key.PageUp && ctrl))
        {
            SelectBy(-1);
        }
        else if (ctrl && TabNumber(key) is { } number)
        {
            // Ctrl+1 to Ctrl+8: that tab; Ctrl+9: the last.
            if (_tabs.Count > 0) Select(_tabs[number == 9 ? _tabs.Count - 1 : Math.Min(number, _tabs.Count) - 1]);
        }
        else
        {
            return;
        }
        e.Handled = true;
    }

    private static int? TabNumber(Key key) => key switch
    {
        >= Key.D1 and <= Key.D9 => key - Key.D0,
        >= Key.NumPad1 and <= Key.NumPad9 => key - Key.NumPad0,
        _ => null,
    };

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
        if (!_isFullScreen || _drag != null || Address.IsKeyboardFocusWithin || SitePopup.IsOpen || ShieldPopup.IsOpen) return;
        Toolbar.Visibility = Visibility.Collapsed;
        RevealStrip.Visibility = Visibility.Visible;
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        if (_drag?.Window == this) EndDrag();
        OpenWindows.Remove(this);
        _sites.SavedSites.CollectionChanged -= OnSavedSitesChanged;
        foreach (var tab in _tabs.ToList())
        {
            Forget(tab);
            tab.Dispose();
        }
        _tabs.Clear();
        base.OnClosed(e);
    }
}
