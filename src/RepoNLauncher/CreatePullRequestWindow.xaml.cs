using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using RepoNLauncher.Services;
using RepoNLauncher.ViewModels;

namespace RepoNLauncher;

/// <summary>Creates a pull request on GitHub from one of a project's branches. DialogResult is true once it's done.</summary>
public partial class CreatePullRequestWindow : Window
{
    public CreatePullRequestWindow(CreatePullRequestViewModel viewModel, IGitHubService gitHub)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;

        viewModel.SignIn = () => new GitHubSignInWindow(gitHub) { Owner = this }.ShowDialog() == true;
        viewModel.OpenLink = url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        viewModel.Finished += () => DialogResult = true;

        SourceInitialized += (_, _) => NativeMethods.UseImmersiveDarkMode(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) => _ = viewModel.LoadAsync();
    }

    public CreatePullRequestViewModel ViewModel { get; }
}
