using System.IO;
using System.Windows;
using System.Windows.Interop;
using RepoNLauncher.ViewModels;

namespace RepoNLauncher;

/// <summary>
/// Asked before files are pasted, or dropped, on a device or this PC: what they are, with thumbnails of the photos and
/// videos, where they're from, and the device and folder they'd go to.
/// </summary>
public partial class PasteWindow : Window
{
    // Twice what's shown, for screens scaled to 200%.
    private const int SingleSize = 520;
    private const int TileSize = 176;

    private readonly CancellationTokenSource _closing = new();

    private PasteWindow(PastePreview preview)
    {
        InitializeComponent();
        DataContext = preview;
        SourceInitialized += (_, _) => NativeMethods.UseImmersiveDarkMode(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) => _ = MakeThumbnailsAsync(preview);
    }

    /// <summary>Asks over the window; true to paste.</summary>
    public static bool Ask(Window owner, PastePreview preview)
    {
        // Files dropped from File Explorer leave it in front.
        owner.Activate();
        return new PasteWindow(preview) { Owner = owner }.ShowDialog() == true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _closing.Cancel();
        base.OnClosed(e);
    }

    // In order, one at a time: they're shown at once with their icons (or the thumbnails made already), and each from a
    // device is an adb command.
    private async Task MakeThumbnailsAsync(PastePreview preview)
    {
        var size = preview.Single != null ? SingleSize : TileSize;
        foreach (var item in preview.Shown)
        {
            if (item.MakeThumbnail is not { } make) continue;
            try
            {
                if (await make(size, _closing.Token) is { } made) item.Thumbnail = made;
            }
            catch (OperationCanceledException)
            {
                return; // closed
            }
            catch (Exception e) when (e is InvalidOperationException or TimeoutException or IOException or UnauthorizedAccessException)
            {
                // the device has gone or is busy: it keeps its icon
            }
        }
    }

    private void Paste_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
