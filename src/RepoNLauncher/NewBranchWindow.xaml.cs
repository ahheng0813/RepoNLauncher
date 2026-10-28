using System.Windows;
using System.Windows.Interop;
using RepoNLauncher.ViewModels;

namespace RepoNLauncher;

/// <summary>Makes a new branch in a project. DialogResult is true once it's made.</summary>
public partial class NewBranchWindow : Window
{
    public NewBranchWindow(NewBranchViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;

        viewModel.Finished += () => DialogResult = true;
        SourceInitialized += (_, _) => NativeMethods.UseImmersiveDarkMode(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) => _ = viewModel.LoadAsync();
    }

    public NewBranchViewModel ViewModel { get; }
}
