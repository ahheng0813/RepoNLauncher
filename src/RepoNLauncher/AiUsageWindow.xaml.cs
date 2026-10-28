using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using RepoNLauncher.ViewModels;

namespace RepoNLauncher;

/// <summary>Shows Claude Code and Codex usage, refreshing every minute while open.</summary>
public partial class AiUsageWindow : Window
{
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMinutes(1) };

    public AiUsageWindow(AiUsageViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        SourceInitialized += (_, _) => NativeMethods.UseImmersiveDarkMode(new WindowInteropHelper(this).Handle);
        _refreshTimer.Tick += (_, _) => _ = viewModel.RefreshAsync();
        Loaded += (_, _) =>
        {
            _ = viewModel.RefreshAsync();
            _refreshTimer.Start();
        };
        Closed += (_, _) => _refreshTimer.Stop();
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
