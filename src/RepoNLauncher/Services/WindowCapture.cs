using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace RepoNLauncher.Services;

/// <summary>
/// Saves what the mirror window shows as a PNG: how a camera photo is taken, since scrcpy only streams the camera.
/// It copies the pixels from the screen, so the window has to be in view.
/// </summary>
public static class WindowCapture
{
    /// <param name="video">The size of the video the window letterboxes, to leave out the black bars; null for all of it.</param>
    public static void SavePng(IntPtr hwnd, (int Width, int Height)? video, string path)
    {
        if (!GetWindowRect(hwnd, out var rect) || !IsWindowVisible(hwnd))
            throw new InvalidOperationException("the camera isn't on screen");

        int x = rect.Left, y = rect.Top, width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) throw new InvalidOperationException("the camera isn't on screen");

        // scrcpy scales the video to fit the window and centres it.
        if (video is { Width: > 0, Height: > 0 } size)
        {
            var scale = Math.Min((double)width / size.Width, (double)height / size.Height);
            var shownWidth = (int)Math.Round(size.Width * scale);
            var shownHeight = (int)Math.Round(size.Height * scale);
            x += (width - shownWidth) / 2;
            y += (height - shownHeight) / 2;
            width = shownWidth;
            height = shownHeight;
        }

        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(x, y, 0, 0, new Size(width, height));
        bitmap.Save(path, ImageFormat.Png);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);
}
