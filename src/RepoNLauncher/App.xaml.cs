using System.Windows;
using RepoNLauncher.Services;
using RepoNLauncher.ViewModels;

namespace RepoNLauncher;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var processRunner = new ProcessRunnerService();
        var git = new GitService(processRunner);
        var flutter = new FlutterService(processRunner);
        var dotNet = new DotNetService();
        var envParser = new EnvironmentParserService();
        var scanner = new ProjectScannerService();
        var configService = new DashboardConfigService();
        var ports = new PortService();
        var ides = new IdeService();
        var scrcpy = new ScrcpyService();
        var gitHub = new GitHubService();
        var aiUsage = new AiUsageViewModel(new ClaudeUsageService(), new CodexUsageService());

        var mainViewModel = new MainViewModel(configService, scanner, git, flutter, dotNet, envParser, processRunner, ports, ides, scrcpy, gitHub, aiUsage);

        var mainWindow = new MainWindow { DataContext = mainViewModel };
        mainWindow.Show();
    }
}
