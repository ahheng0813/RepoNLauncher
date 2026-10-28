using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using RepoNLauncher.ViewModels;

namespace RepoNLauncher;

public partial class MainWindow : Window
{
    private PhoneScreenWindow? _phoneWindow;
    private PcReposWindow? _pcReposWindow;

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
            if (DataContext is MainViewModel vm) vm.PhoneScreen.PropertyChanged += OnPhoneScreenPropertyChanged;
        };
        Closing += (_, _) =>
        {
            if (DataContext is not MainViewModel vm) return;
            vm.PhoneScreen.Shutdown();
            vm.PhoneScreen.IsPoppedOut = false; // closes the pop-out window so the app can exit
        };
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

    // Re-list devices whenever the picker opens, so a phone plugged in a moment ago is already there.
    private void DeviceCombo_DropDownOpened(object sender, EventArgs e) =>
        (DataContext as MainViewModel)?.PhoneScreen.RefreshDevicesCommand.Execute(null);
}
