using System.Windows;
using System.Windows.Interop;

namespace RepoNLauncher;

/// <summary>Asked once after installing: whether to add a desktop icon. Closing it counts as no.</summary>
public partial class DesktopIconWindow : Window
{
    public DesktopIconWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => NativeMethods.UseImmersiveDarkMode(new WindowInteropHelper(this).Handle);
    }

    public bool CreateIcon => DialogResult == true && CreateIconBox.IsChecked == true;

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
