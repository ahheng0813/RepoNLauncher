using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using RepoNLauncher.ViewModels;

namespace RepoNLauncher;

/// <summary>
/// Claude Code and Codex usage in full. The dashboard keeps the figures refreshed for its usage bar, so opening this
/// only checks the plan limits again.
/// </summary>
public partial class AiUsageWindow : Window
{
    public AiUsageWindow(AiUsageViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        SourceInitialized += (_, _) => NativeMethods.UseImmersiveDarkMode(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) => _ = viewModel.RefreshAsync(checkLimits: true);
    }

    private void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string url)
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    // Signing in is the tool's own job; this just opens a console running its sign-in command.
    // /s: cmd strips only the outer quotes, so a quoted program path survives.
    private void SignIn_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string command)
            Process.Start(new ProcessStartInfo("cmd.exe", $"/s /k \"{command}\"") { UseShellExecute = true });
    }
}
