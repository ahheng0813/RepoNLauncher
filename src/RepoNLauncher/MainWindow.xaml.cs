using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using RepoNLauncher.Controls;
using RepoNLauncher.Services;
using RepoNLauncher.ViewModels;

namespace RepoNLauncher;

public partial class MainWindow : Window
{
    // The devices popped out of the phone panel, each in a window of its own.
    private readonly Dictionary<DeviceScreenViewModel, PhoneScreenWindow> _phoneWindows = new();
    private PcReposWindow? _pcReposWindow;
    private CloneReposWindow? _cloneReposWindow;
    private AiUsageWindow? _aiUsageWindow;
    private AgentChatWindow? _agentChatWindow;
    private EmulatorsWindow? _emulatorsWindow;
    private SettingsWindow? _settingsWindow;

    // Keeps the AI usage bar current while the dashboard is open; plan limits are checked again every few minutes.
    private readonly DispatcherTimer _usageTimer = new() { Interval = TimeSpan.FromMinutes(1) };

    // Blinks the cursor after the header's C:\Repo'N'Launcher> prompt. A timer rather than an animation: a running
    // storyboard re-renders the window every frame, which competes with the phone screen mirror.
    private readonly DispatcherTimer _cursorTimer = new();

    // How many devices the phone panel's width is for, and the width it was given for one when it last changed.
    private int _phonePanelFits = 1;
    private double _phoneWidthEach;
    private double _phoneWidthSet = double.NaN;

    public MainWindow()
    {
        InitializeComponent();
        InputBindings.Add(new KeyBinding(new CommunityToolkit.Mvvm.Input.RelayCommand(() => OpenSettings(SettingsPage.DateAndTime)),
            Key.OemComma, ModifierKeys.Control));
        // The phone panel made the default layout wider; never open larger than the screen.
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.UseImmersiveDarkMode(hwnd);
        };
        Loaded += (_, _) =>
        {
            if (DataContext is not MainViewModel vm) return;
            vm.PhoneScreen.PopOutChanged += OnPhonePopOutChanged;
            vm.PhoneScreen.PropertyChanged += OnPhoneScreenPropertyChanged;
            vm.SiteBrowser.WindowRequested += request => BrowserWindow.Open(vm.SiteBrowser, request);
            vm.SiteBrowser.ScreenPicked += BrowserWindow.MoveAllTo;
            // A project taken off the dashboard takes its console window with it.
            vm.Projects.CollectionChanged += (_, _) => RunConsoleWindow.CloseRemoved(vm.Projects);

            _ = vm.AiUsage.RefreshAsync(checkLimits: true);
            vm.Updates.RestartRequested += () => RestartToUpdate(vm);
            vm.Updates.Start();
            if (vm.Updates.InstallFailure is { } failure)
                Dispatcher.BeginInvoke(() => System.Windows.MessageBox.Show(this, failure, "Update not installed",
                    MessageBoxButton.OK, MessageBoxImage.Warning), DispatcherPriority.ApplicationIdle);
            // Once the dashboard has drawn, so the question shows over it rather than before it.
            if (vm.ShouldAskAboutDesktopIcon())
                Dispatcher.BeginInvoke(() => AskAboutDesktopIcon(vm), DispatcherPriority.ApplicationIdle);
            _usageTimer.Tick += (_, _) =>
            {
                if (WindowState != WindowState.Minimized) _ = vm.AiUsage.RefreshAsync();
            };
            _usageTimer.Start();
            StartPromptCursor();
        };
        Closing += (_, _) =>
        {
            _usageTimer.Stop();
            _cursorTimer.Stop();
            BrowserWindow.CloseAll();
            RunConsoleWindow.CloseAll();
            if (DataContext is not MainViewModel vm) return;
            vm.AgentChat.StopAll();
            vm.PhoneScreen.Shutdown();
            vm.PhoneScreen.DockAll(); // closes the pop-out windows so the app can exit
        };
    }

    // At Windows' own caret blink rate, as the console's cursor blinks; it stays lit where blinking is turned off.
    private void StartPromptCursor()
    {
        var blink = System.Windows.Forms.SystemInformation.CaretBlinkTime;
        if (blink <= 0) return;
        _cursorTimer.Interval = TimeSpan.FromMilliseconds(blink);
        _cursorTimer.Tick += (_, _) => PromptCursor.Opacity = PromptCursor.Opacity > 0 ? 0 : 1;
        _cursorTimer.Start();
    }

    // The updater waits for the app to close; closing the window stops chats and the phone mirror on the way out.
    private void RestartToUpdate(MainViewModel vm)
    {
        if (vm.AgentChat.HasRunningChats && System.Windows.MessageBox.Show(this,
                "A chat is still running. Restarting stops it.\n\nRestart and update now?", "Restart to update",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        vm.Updates.ApplyOnExit();
        Close();
    }

    private void Uninstall_Click(object sender, RoutedEventArgs e) => Uninstall(this);

    // The uninstall waits for the app to close; closing the window stops chats and the phone mirror on the way out.
    // From the status bar or Settings, which asks over itself.
    internal void Uninstall(Window owner)
    {
        if (DataContext is not MainViewModel vm) return;
        if (System.Windows.MessageBox.Show(owner,
                "Uninstall Repo'N'Launcher and remove everything it keeps on this PC?\n\n" +
                "That's your project list and settings, the GitHub sign-in, the scrcpy it downloaded, its map, preview and " +
                "browser window caches, temp files, update files and the desktop icon. Your projects' own folders stay as they are." +
                (vm.AgentChat.HasRunningChats ? "\n\nA chat is still running. Uninstalling stops it." : ""),
                "Uninstall Repo'N'Launcher", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        try
        {
            Uninstaller.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            System.Windows.MessageBox.Show(owner, $"Repo'N'Launcher couldn't start uninstalling: {ex.Message}", "Uninstall Repo'N'Launcher",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Close();
    }

    private void AskAboutDesktopIcon(MainViewModel vm)
    {
        var dialog = new DesktopIconWindow { Owner = this };
        dialog.ShowDialog();
        vm.DesktopIconAsked();
        if (!dialog.CreateIcon) return;

        try
        {
            DesktopIcon.Create();
        }
        catch (Exception e)
        {
            System.Windows.MessageBox.Show(this, $"The desktop icon couldn't be created: {e.Message}", "Create Desktop Icon",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnPhonePopOutChanged(DeviceScreenViewModel screen)
    {
        if (screen.IsPoppedOut && !_phoneWindows.ContainsKey(screen))
        {
            var window = new PhoneScreenWindow(screen);
            PlaceBesideOtherPhones(window);
            window.Closing += (_, _) =>
            {
                // Closing the pop-out puts the screen back in the panel rather than losing it.
                _phoneWindows.Remove(screen);
                screen.IsPoppedOut = false;
            };
            _phoneWindows[screen] = window;
            window.Show();
        }
        else if (!screen.IsPoppedOut && _phoneWindows.Remove(screen, out var window))
        {
            window.Close();
        }
    }

    // Another device's window opens to the right of those already open rather than on top of them, while that fits on
    // the screens; otherwise it's centred as usual.
    private void PlaceBesideOtherPhones(Window window)
    {
        var rightmost = _phoneWindows.Values.Where(w => w.WindowState == WindowState.Normal).MaxBy(w => w.Left + w.ActualWidth);
        if (rightmost == null) return;

        var left = rightmost.Left + rightmost.ActualWidth + 8;
        if (left + window.Width > SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth) return;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = left;
        window.Top = rightmost.Top;
    }

    private void OnPhoneScreenPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PhoneScreenViewModel.DockedScreenCount) && sender is PhoneScreenViewModel phone)
            FitPhonePanel(phone.DockedScreenCount);
    }

    // Devices side by side in the phone panel each get about the width one had, so a second one doesn't squeeze the
    // first to half its size, while the project details keep their minimum width.
    private void FitPhonePanel(int docked)
    {
        docked = Math.Max(docked, 1);
        if (docked == _phonePanelFits) return;

        // The width for one: from what the panel was dragged to, unless it's still the width set here last, which the
        // room may have cut short.
        var width = PhoneColumn.Width.Value;
        var each = width == _phoneWidthSet ? _phoneWidthEach : width / _phonePanelFits;
        var room = PhoneColumn.ActualWidth + DetailsColumn.ActualWidth - DetailsColumn.MinWidth;

        _phonePanelFits = docked;
        _phoneWidthEach = each;
        _phoneWidthSet = Math.Max(each, Math.Min(each * docked, room));
        PhoneColumn.Width = new GridLength(_phoneWidthSet);
    }

    // Clicking a device in the phone panel, its screen or its name above it, picks it for the panel's tools.
    private void DockedScreen_ScreenClicked(object? sender, EventArgs e) => SelectDockedScreen(sender);

    private void DockedScreenName_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => SelectDockedScreen(sender);

    private void SelectDockedScreen(object? sender)
    {
        if (sender is FrameworkElement { DataContext: DeviceScreenViewModel screen } && DataContext is MainViewModel vm)
            vm.PhoneScreen.Select(screen);
    }

    // A device that stops showing takes its place in the panel with it; its host would otherwise linger, parked.
    private void DockedScreen_Unloaded(object sender, RoutedEventArgs e) => ((ScreenHost)sender).Dispose();

    // TreeView.SelectedItem is read-only, so selection is pushed to the view model from here. Only projects count:
    // a section can end up selected (collapsing one moves the tree's selection onto it), which shouldn't clear the detail pane.
    private void ProjectTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is ProjectViewModel project && DataContext is MainViewModel vm)
            vm.SelectedProject = project;
    }

    private void EditOpenWithApps_Click(object sender, RoutedEventArgs e) => EditOpenWithApps(this);

    // From the Overview's OPEN IN row or Settings, over whichever asked.
    internal void EditOpenWithApps(Window owner)
    {
        if (DataContext is not MainViewModel vm) return;

        var dialog = new OpenWithAppsWindow(vm.OpenWithApps) { Owner = owner };
        if (dialog.ShowDialog() == true && dialog.Result != null) vm.SaveOpenWithApps(dialog.Result);
    }

    private void SignInToGitHub_Click(object sender, RoutedEventArgs e) => SignInToGitHub();

    internal bool SignInToGitHub(Window? owner = null) =>
        DataContext is MainViewModel vm && new GitHubSignInWindow(vm.GitHub.Service) { Owner = owner ?? this }.ShowDialog() == true;

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => OpenSettings(SettingsPage.DateAndTime);

    // The version in the status bar opens what it's the version of.
    private void OpenAbout_Click(object sender, RoutedEventArgs e) => OpenSettings(SettingsPage.About);

    private void OpenSettings(SettingsPage page)
    {
        if (DataContext is not MainViewModel vm) return;
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(vm.Settings, this);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Show(page);
    }

    // Telling mine from others' needs GitHub, so signing in comes first.
    private void OpenPcRepos_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (!vm.GitHub.IsSignedIn && !SignInToGitHub()) return;

        if (_pcReposWindow != null)
        {
            NativeMethods.BringToFront(_pcReposWindow);
            return;
        }
        _pcReposWindow = new PcReposWindow(vm.PcRepos) { Owner = this };
        _pcReposWindow.Closed += (_, _) => _pcReposWindow = null;
        _pcReposWindow.Show();
    }

    private void OpenCloneRepos_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (!vm.GitHub.IsSignedIn && !SignInToGitHub()) return;

        if (_cloneReposWindow != null)
        {
            NativeMethods.BringToFront(_cloneReposWindow);
            return;
        }
        _cloneReposWindow = new CloneReposWindow(vm.CloneRepos) { Owner = this };
        _cloneReposWindow.Closed += (_, _) => _cloneReposWindow = null;
        _cloneReposWindow.Show();
    }

    private void OpenEmulators_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (_emulatorsWindow != null)
        {
            NativeMethods.BringToFront(_emulatorsWindow);
            return;
        }
        _emulatorsWindow = new EmulatorsWindow(vm.Emulators) { Owner = this };
        _emulatorsWindow.Closed += (_, _) => _emulatorsWindow = null;
        _emulatorsWindow.Show();
    }

    private void OpenAiUsage_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (_aiUsageWindow != null)
        {
            NativeMethods.BringToFront(_aiUsageWindow);
            return;
        }
        _aiUsageWindow = new AiUsageWindow(vm.AiUsage) { Owner = this };
        _aiUsageWindow.Closed += (_, _) => _aiUsageWindow = null;
        _aiUsageWindow.Show();
    }

    // Chats keep running with the window closed; reopening it shows where they got to.
    private void OpenAiChat_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (_agentChatWindow != null)
        {
            NativeMethods.BringToFront(_agentChatWindow);
            return;
        }
        _agentChatWindow = new AgentChatWindow(vm.AgentChat) { Owner = this };
        _agentChatWindow.Closed += (_, _) => _agentChatWindow = null;
        _agentChatWindow.Show();
    }

    private async void NewBranch_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || vm.SelectedProject is not { } project) return;

        var dialog = new NewBranchWindow(project.NewBranch()) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.ViewModel.Result is { } message) await project.BranchCreatedAsync(message);
    }

    // From the selected project's current branch, or another picked in the dialog. Creating one needs the GitHub sign-in;
    // the dialog asks for it.
    private void CreatePullRequest_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || vm.SelectedProject is not { } project) return;
        if (GitHubRemote.Find(project.Path) is not { } repo)
        {
            project.StatusMessage = "This repo isn't on GitHub (it has no GitHub remote), so there's nowhere to open a pull request.";
            return;
        }

        var dialog = new CreatePullRequestWindow(project.NewPullRequest(repo, vm.GitHub.Service), vm.GitHub.Service) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.ViewModel.Created is not { } created) return;

        project.StatusMessage = $"Opened pull request #{created.Number}: {created.Title}";
        Process.Start(new ProcessStartInfo(created.Url) { UseShellExecute = true });
        _ = project.RefreshStatusAsync();
    }

    // Re-list devices whenever the picker opens, so a phone plugged in a moment ago is already there.
    private void DeviceCombo_DropDownOpened(object sender, EventArgs e) =>
        (DataContext as MainViewModel)?.PhoneScreen.RefreshDevicesCommand.Execute(null);

    // Likewise for screens: one plugged in a moment ago is there to open sites on.
    private void SiteTargets_DropDownOpened(object sender, EventArgs e) => (DataContext as MainViewModel)?.SiteBrowser.ListTargets();
}
