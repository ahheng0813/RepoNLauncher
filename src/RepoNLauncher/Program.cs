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
            // A new version may bring a new icon, and Windows would keep showing the cached old one on the desktop,
            // Start menu and taskbar until it next refreshes. Run as the update or install finishes, before the app starts.
            .OnAfterInstallFastCallback(_ => RefreshIcons())
            .OnAfterUpdateFastCallback(_ => RefreshIcons())
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

    // Only a nicety: it must never get in the way of the install or update it runs in.
    private static void RefreshIcons()
    {
        try
        {
            NativeMethods.RefreshShellIcons();
        }
        catch (Exception)
        {
        }
    }
}
