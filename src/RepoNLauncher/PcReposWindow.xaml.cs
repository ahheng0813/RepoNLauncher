using System.Windows;
using System.Windows.Interop;
using RepoNLauncher.ViewModels;

namespace RepoNLauncher;

/// <summary>Lists the git repos on this PC by GitHub owner. A scan keeps going if the window is closed, and its results stay for the session.</summary>
public partial class PcReposWindow : Window
{
    private readonly PcReposViewModel _viewModel;

    public PcReposWindow(PcReposViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;

        SourceInitialized += (_, _) => NativeMethods.UseImmersiveDarkMode(new WindowInteropHelper(this).Handle);
        // Opening the window is asking for the list, so the first time it scans straight away.
        Loaded += (_, _) =>
        {
            if (viewModel.Repos.Count == 0 && viewModel.ScanCommand.CanExecute(null)) viewModel.ScanCommand.Execute(null);
        };
        Activated += (_, _) => viewModel.RefreshDashboardFlags();
    }

    private void SignIn_Click(object sender, RoutedEventArgs e)
    {
        if (new GitHubSignInWindow(_viewModel.Account.Service) { Owner = this }.ShowDialog() == true &&
            _viewModel.Repos.Count == 0 && _viewModel.ScanCommand.CanExecute(null))
            _viewModel.ScanCommand.Execute(null);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
