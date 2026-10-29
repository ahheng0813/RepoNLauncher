using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using RepoNLauncher.Services;
using RepoNLauncher.ViewModels;

namespace RepoNLauncher;

public partial class MainWindow : Window
{
    private PhoneScreenWindow? _phoneWindow;
    private PcReposWindow? _pcReposWindow;
    private CloneReposWindow? _cloneReposWindow;
    private AiUsageWindow? _aiUsageWindow;
    private AgentChatWindow? _agentChatWindow;
    private EmulatorsWindow? _emulatorsWindow;

    // Keeps the AI usage bar current while the dashboard is open; plan limits are checked again every few minutes.
    private readonly DispatcherTimer _usageTimer = new() { Interval = TimeSpan.FromMinutes(1) };

    public MainWindow()
    {
        InitializeComponent();
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
            vm.PhoneScreen.PropertyChanged += OnPhoneScreenPropertyChanged;

            _ = vm.AiUsage.RefreshAsync(checkLimits: true);
            vm.Updates.RestartRequested += () => RestartToUpdate(vm);
            vm.Updates.Start();
            // Once the dashboard has drawn, so the question shows over it rather than before it.
            if (vm.ShouldAskAboutDesktopIcon())
                Dispatcher.BeginInvoke(() => AskAboutDesktopIcon(vm), DispatcherPriority.ApplicationIdle);
            _usageTimer.Tick += (_, _) =>
            {
                if (WindowState != WindowState.Minimized) _ = vm.AiUsage.RefreshAsync();
            };
            _usageTimer.Start();
        };
        Closing += (_, _) =>
        {
            _usageTimer.Stop();
            if (DataContext is not MainViewModel vm) return;
            vm.AgentChat.StopAll();
            vm.PhoneScreen.Shutdown();
            vm.PhoneScreen.IsPoppedOut = false; // closes the pop-out window so the app can exit
        };
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

    private void OnPhoneScreenPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PhoneScreenViewModel.IsPoppedOut) || sender is not PhoneScreenViewModel phone) return;

        if (phone.IsPoppedOut && _phoneWindow == null)
        {
            var window = new PhoneScreenWindow(phone);
            window.Closing += (_, _) =>
            {
                // Closing the pop-out puts the screen back in the panel rather than losing it.
                _phoneWindow = null;
                phone.IsPoppedOut = false;
            };
            _phoneWindow = window;
            window.Show();
        }
        else if (!phone.IsPoppedOut && _phoneWindow != null)
        {
            var window = _phoneWindow;
            _phoneWindow = null;
            window.Close();
        }
    }

    // TreeView.SelectedItem is read-only, so selection is pushed to the view model from here. Only projects count:
    // a section can end up selected (collapsing one moves the tree's selection onto it), which shouldn't clear the detail pane.
    private void ProjectTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is ProjectViewModel project && DataContext is MainViewModel vm)
            vm.SelectedProject = project;
    }

    // Like a terminal, the Run console follows new output while it's scrolled to the bottom, so a prompt is in view
    // when it asks; scrolled up to read, it stays put.
    private void Console_ScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange <= 0 || e.OriginalSource is not System.Windows.Controls.ScrollViewer viewer) return;
        var wasAtBottom = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - e.ExtentHeightChange - 1;
        if (wasAtBottom) viewer.ScrollToEnd();
    }

    private void EditOpenWithApps_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        var dialog = new OpenWithAppsWindow(vm.OpenWithApps) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result != null) vm.SaveOpenWithApps(dialog.Result);
    }

    private void SignInToGitHub_Click(object sender, RoutedEventArgs e) => SignInToGitHub();

    private bool SignInToGitHub() =>
        DataContext is MainViewModel vm && new GitHubSignInWindow(vm.GitHub.Service) { Owner = this }.ShowDialog() == true;

    // Telling mine from others' needs GitHub, so signing in comes first.
    private void OpenPcRepos_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (!vm.GitHub.IsSignedIn && !SignInToGitHub()) return;

        if (_pcReposWindow != null)
        {
            _pcReposWindow.Activate();
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
            _cloneReposWindow.Activate();
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
            _emulatorsWindow.Activate();
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
            _aiUsageWindow.Activate();
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
            if (_agentChatWindow.WindowState == WindowState.Minimized) _agentChatWindow.WindowState = WindowState.Normal;
            _agentChatWindow.Activate();
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
}
