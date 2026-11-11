using System.Windows;
using System.Windows.Interop;
using RepoNLauncher.ViewModels;

namespace RepoNLauncher;

/// <summary>Lists the Android emulators on this PC to start, shut down, rename or delete, and makes new ones.</summary>
public partial class EmulatorsWindow : Window
{
    public EmulatorsWindow(EmulatorsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        SourceInitialized += (_, _) => NativeMethods.UseImmersiveDarkMode(new WindowInteropHelper(this).Handle);
    }
}
