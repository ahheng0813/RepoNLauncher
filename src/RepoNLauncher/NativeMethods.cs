using System.Runtime.InteropServices;

namespace RepoNLauncher;

/// <summary>
/// Windows calls: the console-style title bar that matches the app's theme, refreshing the icons File Explorer shows,
/// Explorer's name order, and where the Downloads folder is.
/// </summary>
internal static class NativeMethods
{
    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0;

    /// <summary>
    /// Tells File Explorer that icons have changed, so the desktop, Start menu and taskbar draw the app's icon again
    /// rather than the previous version's, which Windows otherwise keeps showing from its icon cache.
    /// </summary>
    public static void RefreshShellIcons() => SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_20H1 = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_1809 = 19;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;
    private const int DWMWCP_DONOTROUND = 1;

    // COLORREFs (0x00BBGGRR) for the theme's Bg.App, Text.Primary and Border.Subtle
    private const int ConsoleBlack = 0x000C0C0C;
    private const int ConsoleGray = 0x00CCCCCC;
    private const int BoxLine = 0x003A3A3A;

    /// <summary>
    /// A dark title bar, and on Windows 11 a console window's: black caption, gray title, square corners and a thin
    /// gray edge. Older Windows ignores what it doesn't know and keeps the plain dark one.
    /// </summary>
    public static void UseImmersiveDarkMode(IntPtr hwnd)
    {
        int enabled = 1;
        if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_20H1, ref enabled, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_1809, ref enabled, sizeof(int));
        }

        int caption = ConsoleBlack, text = ConsoleGray, border = BoxLine, corners = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));
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
