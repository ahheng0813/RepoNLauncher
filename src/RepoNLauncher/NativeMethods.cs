using System.Runtime.InteropServices;

namespace RepoNLauncher;

/// <summary>
/// Windows calls: the immersive dark title bar that matches the app's dark theme, Explorer's name order, and where the
/// Downloads folder is.
/// </summary>
internal static class NativeMethods
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_20H1 = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_1809 = 19;

    public static void UseImmersiveDarkMode(IntPtr hwnd)
    {
        int enabled = 1;
        if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_20H1, ref enabled, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_1809, ref enabled, sizeof(int));
        }
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string a, string b);

    /// <summary>Compares names as File Explorer sorts them: "IMG_2" before "IMG_10", whatever the capitals.</summary>
    public static readonly IComparer<string> ExplorerNameOrder = Comparer<string>.Create(StrCmpLogicalW);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(in Guid id, uint flags, IntPtr token, out string path);

    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>The Downloads folder, wherever it's been moved to.</summary>
    public static string DownloadsFolder =>
        SHGetKnownFolderPath(DownloadsFolderId, 0, IntPtr.Zero, out var path) == 0
            ? path
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
}
