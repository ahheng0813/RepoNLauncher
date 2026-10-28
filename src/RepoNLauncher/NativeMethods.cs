using System.Runtime.InteropServices;

namespace RepoNLauncher;

/// <summary>Enables the Windows 10/11 immersive dark title bar so it matches the app's dark theme.</summary>
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
}
