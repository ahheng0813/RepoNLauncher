using RepoNLauncher.Services;
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
        VelopackApp.Build()
            .SetArgs(args)
            .OnRestarted(_ => UpdateService.StartedByUpdater = true)
            .Run();

        // Started from its icon, the app's working folder is the install's current folder, and every program it starts
        // (an IDE, the emulator, a console) inherits it. One left open holds that folder, so the updater, which renames
        // it to swap in the new version, can't: the update fails without a word and the old version starts again.
        Environment.CurrentDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
