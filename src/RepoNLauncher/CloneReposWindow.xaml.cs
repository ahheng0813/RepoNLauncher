using System.Windows;
using System.Windows.Interop;
using RepoNLauncher.ViewModels;

namespace RepoNLauncher;

/// <summary>Lists the GitHub account's repos to clone. Cloning keeps going if the window is closed, and the list stays for the session.</summary>
public partial class CloneReposWindow : Window
{
    private readonly CloneReposViewModel _viewModel;

    public CloneReposWindow(CloneReposViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;

        SourceInitialized += (_, _) => NativeMethods.UseImmersiveDarkMode(new WindowInteropHelper(this).Handle);
        // Opening the window is asking for the list, so the first time it loads straight away.
        Loaded += (_, _) =>
        {
            if (viewModel.Repos.Count == 0 && viewModel.RefreshCommand.CanExecute(null)) viewModel.RefreshCommand.Execute(null);
        };
        Activated += (_, _) => viewModel.ApplyLocal();
    }

    // Each repo's picker starts with just its default branch; opening it gets the rest from GitHub.
    private void Branches_DropDownOpened(object? sender, EventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CloneRepoViewModel repo }) _ = repo.LoadBranchesAsync();
    }

    private void SignIn_Click(object sender, RoutedEventArgs e)
    {
        if (new GitHubSignInWindow(_viewModel.Account.Service) { Owner = this }.ShowDialog() == true &&
            _viewModel.Repos.Count == 0 && _viewModel.RefreshCommand.CanExecute(null))
            _viewModel.RefreshCommand.Execute(null);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
