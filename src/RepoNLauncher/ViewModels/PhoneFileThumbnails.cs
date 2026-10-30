using System.IO;
using System.Windows.Media.Imaging;
using RepoNLauncher.Services;

namespace RepoNLauncher.ViewModels;

/// <summary>
/// What's in the pictures and videos of the Phone files icon view: made as they come into view, a few at a time and the
/// latest first, and kept for when they're shown again.
/// </summary>
public sealed class PhoneFileThumbnails(IDeviceToolsService tools, string serial)
{
    /// <summary>How big they're made: twice the icon view's pictures, for screens scaled to 200%.</summary>
    public const int Size = 176;

    // Each is an adb command, and a phone answers a few at once about as fast as one.
    private const int AtOnce = 3;
    // Most camera JPEGs keep a small copy in their first 64 KB, which is far quicker to fetch than the whole photo.
    private const int ExifBytes = 128 * 1024;
    // Bigger pictures show the gallery's small copy, rather than be copied whole for one.
    private const long WholePictureLimit = 40L * 1024 * 1024;
    private const long KeptBytes = 64L * 1024 * 1024;

    // For every window, so a folder opened again shows at once. A file changed since has another key, so it's made again.
    private static readonly object Lock = new();
    private static readonly Dictionary<string, LinkedListNode<(string Key, byte[]? Image)>> Memory = new();
    private static readonly LinkedList<(string Key, byte[]? Image)> RecentlyUsed = new();
    private static long _keptBytes;

    private readonly List<PhoneFileItem> _waiting = new(); // the latest last, made first
    private readonly HashSet<PhoneFileItem> _shown = new();
    private readonly CancellationTokenSource _closing = new();
    private int _making;

    /// <summary>Shows what's in the item now it's in view, making its thumbnail first if need be.</summary>
    public void Show(PhoneFileItem item)
    {
        if (!item.IsPicture && !item.IsVideo) return;
        _shown.Add(item);
        if (TryKept(Key(item), out var image))
        {
            item.Thumbnail = Decode(image);
            return;
        }
        _waiting.Remove(item);
        _waiting.Add(item);
        MakeNext();
    }

    /// <summary>Lets the item's thumbnail go now it's out of view; one not made yet waits till it's back.</summary>
    public void Hide(PhoneFileItem item)
    {
        _shown.Remove(item);
        _waiting.Remove(item);
        item.Thumbnail = null;
    }

    /// <summary>The item's thumbnail (a JPEG or PNG), if it's been made.</summary>
    public byte[]? Kept(PhoneFileItem item) => TryKept(Key(item), out var image) ? image : null;

    public void Close() => _closing.Cancel();

    private void MakeNext()
    {
        while (_making < AtOnce && _waiting.Count > 0 && !_closing.IsCancellationRequested)
        {
            var item = _waiting[^1];
            _waiting.RemoveAt(_waiting.Count - 1);
            _making++;
            _ = MakeAsync(item);
        }
    }

    private async Task MakeAsync(PhoneFileItem item)
    {
        try
        {
            // Another window may have made it meanwhile.
            var key = Key(item);
            if (!TryKept(key, out var image))
            {
                image = await FetchAsync(item, _closing.Token);
                Keep(key, image);
            }
            if (_shown.Contains(item)) item.Thumbnail = Decode(image);
        }
        catch (OperationCanceledException)
        {
            // the window closed
        }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException or IOException)
        {
            // The phone has gone or is busy: it keeps its icon, and is tried again when it next comes into view.
        }
        finally
        {
            _making--;
            MakeNext();
        }
    }

    // A picture's small copy is quickest from its EXIF, then from the whole picture. Videos, and pictures Windows can't
    // read (HEIC, without its extension), have the gallery's.
    private async Task<byte[]?> FetchAsync(PhoneFileItem item, CancellationToken ct)
    {
        var file = item.File;
        if (item.IsPicture)
        {
            if (Path.GetExtension(file.Name).ToLowerInvariant() is ".jpg" or ".jpeg")
            {
                var start = await tools.ReadFileStartAsync(serial, file.Path, ExifBytes, ct);
                if (PhonePictures.ExifThumbnail(start) is var (exif, orientation)
                    && await Task.Run(() => PhonePictures.MakeThumbnail(exif, Size, orientation), ct) is { } small)
                    return small;
            }
            if (file.Size <= WholePictureLimit)
            {
                using var data = new MemoryStream((int)file.Size);
                await tools.ReadFileAsync(serial, file.Path, data, null, ct);
                var whole = data.ToArray();
                if (await Task.Run(() => PhonePictures.MakeThumbnail(whole, Size), ct) is { } small) return small;
            }
        }

        var gallery = await tools.GetMediaThumbnailAsync(serial, file.Path, item.IsVideo, ct);
        return gallery == null ? null : await Task.Run(() => PhonePictures.MakeThumbnail(gallery, Size), ct);
    }

    // Kept upright already.
    private static BitmapSource? Decode(byte[]? image) => image == null ? null : PhonePictures.Decode(image, Size, orientation: 1)?.Image;

    private string Key(PhoneFileItem item) => $"{serial}|{item.File.Path}|{item.File.Size}|{item.File.Modified.Ticks}";

    // A file with no thumbnail is kept too, as null, so it isn't asked for again and again.
    private static bool TryKept(string key, out byte[]? image)
    {
        lock (Lock)
        {
            if (!Memory.TryGetValue(key, out var node))
            {
                image = null;
                return false;
            }
            RecentlyUsed.Remove(node);
            RecentlyUsed.AddFirst(node);
            image = node.Value.Image;
            return true;
        }
    }

    private static void Keep(string key, byte[]? image)
    {
        lock (Lock)
        {
            if (Memory.ContainsKey(key)) return;
            Memory[key] = RecentlyUsed.AddFirst((key, image));
            _keptBytes += Weight(image);
            while (_keptBytes > KeptBytes && RecentlyUsed.Last is { } oldest)
            {
                Memory.Remove(oldest.Value.Key);
                _keptBytes -= Weight(oldest.Value.Image);
                RecentlyUsed.RemoveLast();
            }
        }
    }

    // Its bytes, and about what its key and place in the lists take.
    private static long Weight(byte[]? image) => (image?.Length ?? 0) + 256;
}
