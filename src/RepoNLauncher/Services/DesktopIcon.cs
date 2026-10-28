using System.IO;
using System.Runtime.InteropServices;
using Velopack.Locators;

namespace RepoNLauncher.Services;

/// <summary>
/// The installed app's desktop icon. The installers only add a Start menu icon, since neither can offer a choice, so the
/// app asks on its first start whether to add one here. It's made like Velopack's own shortcuts, so uninstalling
/// removes it with them.
/// </summary>
public static class DesktopIcon
{
    /// <summary>
    /// The launcher in the install folder that Velopack's shortcuts open: it starts whichever version is current.
    /// It's named after the app's title (Repo'N'Launcher.exe), as the icon is. Null when the app isn't installed.
    /// </summary>
    private static string? LauncherPath
    {
        get
        {
            var root = VelopackLocator.IsCurrentSet ? VelopackLocator.Current.RootAppDir : null;
            if (root == null || !Directory.Exists(root)) return null;
            return Directory.EnumerateFiles(root, "*.exe")
                .FirstOrDefault(f => !Path.GetFileName(f).Equals("Update.exe", StringComparison.OrdinalIgnoreCase));
        }
    }

    private static string? LinkPath => LauncherPath is { } launcher
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Path.GetFileNameWithoutExtension(launcher) + ".lnk")
        : null;

    public static bool Exists => LinkPath is { } link && File.Exists(link);

    public static void Create()
    {
        var launcher = LauncherPath ?? throw new InvalidOperationException("The app isn't installed.");
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host isn't available.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            var link = shell.CreateShortcut(LinkPath!);
            link.TargetPath = launcher;
            link.WorkingDirectory = Path.GetDirectoryName(launcher)!;
            link.IconLocation = Environment.ProcessPath + ",0";
            link.Save();
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }
}
