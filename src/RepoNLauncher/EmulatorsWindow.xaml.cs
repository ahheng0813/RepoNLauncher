using System.Windows;
using System.Windows.Interop;
using RepoNLauncher.ViewModels;

namespace RepoNLauncher;

/// <summary>Lists the Android emulators on this PC to start, and makes new ones.</summary>
public partial class EmulatorsWindow : Window
{
    private readonly EmulatorsViewModel _viewModel;

    public EmulatorsWindow(EmulatorsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        SourceInitialized += (_, _) => NativeMethods.UseImmersiveDarkMode(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) => _ = viewModel.RefreshAsync();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = _viewModel.RefreshAsync();
}
