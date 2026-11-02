using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.CSharp.RuntimeBinder;

namespace RepoNLauncher.Services;

/// <summary>
/// File Explorer's side of copying files between devices and this PC: where files dragged out of the app were dropped,
/// and thumbnails of the files on this PC being sent.
/// </summary>
public static class WindowsShell
{
    private const uint GA_ROOT = 2;
    private const int SIIGBF_THUMBNAILONLY = 0x8;

    // The browser of a File Explorer window, or of one of its tabs.
    private static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");

    /// <summary>The window under the mouse pointer when it's another program's; zero when it's one of the app's own.</summary>
    public static IntPtr OtherWindowAtPointer()
    {
        if (!GetCursorPos(out var point)) return IntPtr.Zero;
        var window = GetAncestor(WindowFromPoint(point), GA_ROOT);
        if (window == IntPtr.Zero) return IntPtr.Zero;
        GetWindowThreadProcessId(window, out var process);
        return process == Environment.ProcessId ? IntPtr.Zero : window;
    }

    /// <summary>
    /// The folder a File Explorer window shows, or the desktop's for the desktop; null for any other window, or for one
    /// showing something that isn't a folder on a drive (Home, This PC, a library).
    /// </summary>
    public static string? FolderOf(IntPtr window)
    {
        var name = new StringBuilder(64);
        GetClassName(window, name, name.Capacity);
        switch (name.ToString())
        {
            case "Progman" or "WorkerW":
                return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            case not "CabinetWClass":
                return null;
        }
        if (Type.GetTypeFromProgID("Shell.Application") is not { } shellType) return null;

        // Each tab of a Windows 11 File Explorer window is listed as a window of its own, with the same handle. The tab
        // in front comes first among the window's tabs.
        var front = FindWindowEx(window, IntPtr.Zero, "ShellTabWindowClass", null);
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic open = shell.Windows();
            int count = open.Count;
            for (var i = 0; i < count; i++)
            {
                object? explorer = open.Item(i);
                if (explorer == null) continue;
                long handle = Convert.ToInt64(((dynamic)explorer).HWND);
                if (handle != window.ToInt64()) continue;
                if (front != IntPtr.Zero && TabOf(explorer) is var tab && tab != IntPtr.Zero && tab != front) continue;
                string path = ((dynamic)explorer).Document.Folder.Self.Path;
                return Path.IsPathFullyQualified(path) && Directory.Exists(path) ? path : null;
            }
            return null;
        }
        catch (Exception e) when (e is COMException or RuntimeBinderException or InvalidCastException)
        {
            return null;
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    // The window of the File Explorer tab, which is the window itself when it has none.
    private static IntPtr TabOf(object explorer)
    {
        if (explorer is not IComServiceProvider services) return IntPtr.Zero;
        var service = SID_STopLevelBrowser;
        var id = typeof(IShellBrowser).GUID;
        return services.QueryService(ref service, ref id, out var browser) == 0 && browser is IShellBrowser shellBrowser
                                                                               && shellBrowser.GetWindow(out var tab) == 0
            ? tab
            : IntPtr.Zero;
    }

    /// <summary>
    /// The names of files, as File Explorer's FileGroupDescriptorW, so it knows files are being dragged and shows it would
    /// copy them. The app copies them itself once they're dropped, so this has their names and nothing more.
    /// </summary>
    public static MemoryStream FileDescriptors(IReadOnlyList<(string Name, bool IsFolder)> files)
    {
        const int FD_ATTRIBUTES = 0x4, FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80, MAX_PATH = 260;
        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: true))
        {
            writer.Write(files.Count);
            foreach (var (name, isFolder) in files)
            {
                writer.Write(FD_ATTRIBUTES);
                writer.Write(new byte[16 + 8 + 8]); // class ID, size and place, for icons
                writer.Write(isFolder ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL);
                writer.Write(new byte[3 * 8 + 2 * 4]); // three times and the size, which it isn't told
                var chars = new char[MAX_PATH];
                name.AsSpan(0, Math.Min(name.Length, MAX_PATH - 1)).CopyTo(chars);
                writer.Write(chars);
            }
        }
        return stream;
    }

    /// <summary>The thumbnail File Explorer shows for a file, such as a video's first frames; null when it has none.</summary>
    public static BitmapSource? Thumbnail(string path, int size)
    {
        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory);
            try
            {
                if (factory.GetImage(new NativeSize { Width = size, Height = size }, SIIGBF_THUMBNAILONLY, out var bitmap) != 0) return null;
                try
                {
                    var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    image.Freeze();
                    return image;
                }
                finally
                {
                    DeleteObject(bitmap);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        catch (Exception e) when (e is COMException or FileNotFoundException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width, Height;
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IComServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid service, ref Guid id, [MarshalAs(UnmanagedType.IUnknown)] out object? result);
    }

    // Only the method it has from IOleWindow, which comes first.
    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        [PreserveSig]
        int GetWindow(out IntPtr window);
    }

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, int flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext,
        [MarshalAs(UnmanagedType.LPStruct)] Guid id, out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int process);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? title);
}
