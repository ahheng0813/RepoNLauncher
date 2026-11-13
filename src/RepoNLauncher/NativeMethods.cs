using System.Runtime.InteropServices;

namespace RepoNLauncher;

/// <summary>
/// Windows calls: the console-style title bar that matches the app's theme, bringing a window to the front, keeping
/// windows on top and moving them as the mouse drags, the mouse and keys held, refreshing the icons File Explorer shows,
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

    /// <summary>
    /// A window that's open already, shown again as opening it would show it: restored if it was minimized (maximized
    /// again if it was maximized), in front of every other window, and active. Activate alone does none of that for a
    /// minimized window, and leaves it behind others when Windows won't hand it the focus, so it's raised through the
    /// topmost band and straight back, which Windows always allows. A window pinned on top stays pinned.
    /// </summary>
    public static void BringToFront(System.Windows.Window window)
    {
        if (!window.IsVisible) window.Show();
        if (window.WindowState == System.Windows.WindowState.Minimized) System.Windows.SystemCommands.RestoreWindow(window);

        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero && !window.Topmost)
        {
            const uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE;
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, flags);
            SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, flags);
        }
        window.Activate();
    }

    private const uint SWP_NOZORDER = 0x0004;

    /// <summary>
    /// Puts a window of any process's in the topmost band, over windows pinned on top, or takes it out again; it isn't
    /// activated either way.
    /// </summary>
    public static void SetTopmost(IntPtr hwnd, bool topmost) =>
        SetWindowPos(hwnd, topmost ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    /// <summary>Moves a window by so many device pixels, without activating it or changing its place among the others.</summary>
    public static void MoveWindowBy(IntPtr hwnd, int dx, int dy)
    {
        if ((dx == 0 && dy == 0) || !GetWindowRect(hwnd, out var rect)) return;
        SetWindowPos(hwnd, IntPtr.Zero, rect.Left + dx, rect.Top + dy, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out CursorPoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint
    {
        public int X, Y;
    }

    /// <summary>Where the mouse is on the screen, in device pixels, as PointToScreen and PointFromScreen take them.</summary>
    public static System.Windows.Point CursorPosition() => GetCursorPos(out var point) ? new(point.X, point.Y) : default;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private const int SM_SWAPBUTTON = 23, VK_LBUTTON = 0x01, VK_RBUTTON = 0x02;

    /// <summary>
    /// Whether the main mouse button is held now, wherever the mouse is: over another window, or another program's, too.
    /// With the buttons swapped in Windows' settings, that's the right one.
    /// </summary>
    public static bool IsPrimaryButtonDown() =>
        (GetAsyncKeyState(GetSystemMetrics(SM_SWAPBUTTON) != 0 ? VK_RBUTTON : VK_LBUTTON) & 0x8000) != 0;

    /// <summary>Whether the key is held now, even while another process (such as a page's browser) has the keyboard.</summary>
    public static bool IsKeyDown(System.Windows.Input.Key key) =>
        (GetAsyncKeyState(System.Windows.Input.KeyInterop.VirtualKeyFromKey(key)) & 0x8000) != 0;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    private static extern IntPtr GetTopWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    private const uint GW_HWNDNEXT = 2, GW_OWNER = 4;
    private const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    private const long WS_CHILD = 0x40000000, WS_CAPTION = 0x00C00000, WS_EX_TOOLWINDOW = 0x00000080;

    /// <summary>The windows on the screen, the one in front first.</summary>
    public static IEnumerable<IntPtr> WindowsFrontToBack()
    {
        for (var hwnd = GetTopWindow(IntPtr.Zero); hwnd != IntPtr.Zero; hwnd = GetWindow(hwnd, GW_HWNDNEXT)) yield return hwnd;
    }

    /// <summary>
    /// A window of its own, as the taskbar shows them: with a title bar, not part of another window, not a tool window,
    /// and not owned by one (an owned window already stays over its owner).
    /// </summary>
    public static bool IsAppWindow(IntPtr hwnd)
    {
        var style = (long)GetWindowLongPtr(hwnd, GWL_STYLE);
        var extended = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        return (style & WS_CHILD) == 0 && (style & WS_CAPTION) == WS_CAPTION && (extended & WS_EX_TOOLWINDOW) == 0
               && GetWindow(hwnd, GW_OWNER) == IntPtr.Zero;
    }

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc proc, uint processId,
        uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    private const uint EVENT_OBJECT_SHOW = 0x8002, WINEVENT_OUTOFCONTEXT = 0;

    private static IntPtr _shownHook;
    private static uint _shownProcess;

    // Kept here, as Windows calls it long after it was handed over.
    private static WinEventProc? _shownProc;
    private static Action<IntPtr>? _onShown;

    /// <summary>
    /// Calls <paramref name="onShown"/> on this thread with each window of the process's shown from now on (whole
    /// windows, not their parts), instead of those of the process it was watching.
    /// </summary>
    public static void WatchWindowsShown(uint processId, Action<IntPtr> onShown)
    {
        _onShown = onShown;
        if (processId == _shownProcess) return;
        if (_shownHook != IntPtr.Zero) UnhookWinEvent(_shownHook);
        _shownProc ??= (_, _, hwnd, idObject, idChild, _, _) =>
        {
            if (idObject == 0 && idChild == 0 && hwnd != IntPtr.Zero) _onShown?.Invoke(hwnd);
        };
        _shownHook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _shownProc, processId, 0, WINEVENT_OUTOFCONTEXT);
        _shownProcess = _shownHook != IntPtr.Zero ? processId : 0;
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
