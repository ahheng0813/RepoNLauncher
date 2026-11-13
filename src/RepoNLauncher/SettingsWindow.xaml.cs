using System.Windows;
using System.Windows.Interop;
using RepoNLauncher.ViewModels;

namespace RepoNLauncher;

/// <summary>The Settings tabs, in their order.</summary>
public enum SettingsPage { DateAndTime, General, Browser, Emulators, Updates, About }

/// <summary>
/// The app's settings, its version and updates, and where it comes from. Changes apply as they're made, so there's
/// only Close. Signing in, the open-in apps and uninstalling are the dashboard's own, so they go through it.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly MainWindow _dashboard;

    public SettingsWindow(SettingsViewModel viewModel, MainWindow dashboard)
    {
        InitializeComponent();
        DataContext = viewModel;
        _dashboard = dashboard;
        Owner = dashboard;

        SourceInitialized += (_, _) => NativeMethods.UseImmersiveDarkMode(new WindowInteropHelper(this).Handle);
        // The desktop icon may have been deleted, and the clock has moved on, since it was last open.
        Activated += (_, _) =>
        {
            viewModel.RefreshDesktopIcon();
            viewModel.RefreshPreview();
        };
    }

    public void Show(SettingsPage page)
    {
        Tabs.SelectedIndex = (int)page;
        if (IsVisible) NativeMethods.BringToFront(this);
        else Show();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void SignIn_Click(object sender, RoutedEventArgs e) => _dashboard.SignInToGitHub(this);

    private void EditOpenWithApps_Click(object sender, RoutedEventArgs e) => _dashboard.EditOpenWithApps(this);

    private void Uninstall_Click(object sender, RoutedEventArgs e) => _dashboard.Uninstall(this);

    // Lists the screens and the default browser again: one may have been plugged in, or made the default, since.
    private void SiteTargets_DropDownOpened(object sender, EventArgs e) => (DataContext as SettingsViewModel)?.SiteBrowser.ListTargets();
}
