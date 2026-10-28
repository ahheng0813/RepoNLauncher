using Velopack;

namespace RepoNLauncher;

/// <summary>
/// Starts the app. The installer and updater run the exe with their own arguments to finish an install, update or
/// uninstall; Velopack handles those and exits before any window opens. It also applies an update that was downloaded
/// last time but not applied yet.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().SetArgs(args).Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
