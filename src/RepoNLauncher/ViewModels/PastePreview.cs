using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RepoNLauncher.Services;

namespace RepoNLauncher.ViewModels;

/// <summary>A file or folder in the paste window: its icon, then its thumbnail once it's made.</summary>
public partial class PasteItem(string name, bool isFolder, long? bytes) : ObservableObject
{
    // Bigger pictures on this PC show File Explorer's thumbnail, rather than be read whole for one.
    private const long WholePictureLimit = 60L * 1024 * 1024;

    public string Name { get; } = name;
    public bool IsFolder { get; } = isFolder;

    /// <summary>A file's size; null for a folder, whose size isn't known.</summary>
    public long? Bytes { get; } = isFolder ? null : bytes;

    public string Size => Bytes is { } size ? FileSize.Format(size) : "";
    public bool IsPicture => !IsFolder && PhoneFileItem.IsPictureName(Name);
    public bool IsVideo => !IsFolder && PhoneFileItem.IsVideoName(Name);
    public string Glyph => PhoneFileItem.GlyphFor(Name, IsFolder);

    /// <summary>The photo, or what's in the video; null shows its icon.</summary>
    [ObservableProperty] private ImageSource? _thumbnail;

    /// <summary>
    /// Makes the thumbnail, about as big as asked, for the paste window once it's open, to replace any shown till then;
    /// null for none.
    /// </summary>
    public Func<int, CancellationToken, Task<ImageSource?>>? MakeThumbnail { get; init; }

    /// <summary>
    /// A file or folder on a device: the picture given (the preview pane's, say), or else the thumbnail the files window
    /// made for it, till a picture as big as is shown is made.
    /// </summary>
    public static PasteItem OnDevice(PhoneFileItem item, PhoneFileThumbnails thumbnails, ImageSource? picture = null) =>
        new(item.Name, item.IsFolder, item.File.Size)
        {
            Thumbnail = picture ?? thumbnails.KeptImage(item),
            MakeThumbnail = picture == null && (item.IsPicture || item.IsVideo)
                ? async (size, ct) => await thumbnails.GetAsync(item, size, ct)
                : null,
        };

    /// <summary>A file or folder on this PC: a picture shows itself, a video File Explorer's thumbnail.</summary>
    public static PasteItem OnPc(string path)
    {
        var isFolder = Directory.Exists(path);
        var name = Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } fileName ? fileName : path;
        long? bytes = null;
        try
        {
            if (!isFolder) bytes = new FileInfo(path).Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // its size isn't shown
        }

        return new PasteItem(name, isFolder, bytes)
        {
            MakeThumbnail = isFolder ? null
                : PhoneFileItem.IsPictureName(name) ? (size, ct) => Task.Run(() => PictureOnPc(path, bytes, size), ct)
                : PhoneFileItem.IsVideoName(name) ? (size, ct) => Task.Run(() => (ImageSource?)WindowsShell.Thumbnail(path, size), ct)
                : null,
        };
    }

    // Upright, as the camera was held; File Explorer's thumbnail for a picture too big to read, or one this PC can't.
    private static ImageSource? PictureOnPc(string path, long? bytes, int size)
    {
        if (bytes <= WholePictureLimit)
        {
            try
            {
                if (PhonePictures.Decode(File.ReadAllBytes(path), size) is { } picture) return picture.Image;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // File Explorer may still have a thumbnail of it
            }
        }
        return WindowsShell.Thumbnail(path, size);
    }
}

/// <summary>
/// What the paste window asks about before files are pasted or dropped: what they are, with thumbnails of photos and
/// videos, where they're from, and the device (or this PC) and folder they'd go to.
/// </summary>
public class PastePreview
{
    private const int MostShown = 12;

    public PastePreview(IReadOnlyList<PasteItem> items, string from, string toPlace, string toFolder)
    {
        Items = items;
        Shown = items.Take(MostShown).ToList();
        From = from;
        Question = $"Do you want to paste {What(items)} at {toPlace}, {toFolder}?";
        More = items.Count > MostShown ? $"and {items.Count - MostShown} more" : "";

        var bytes = items.Sum(i => i.Bytes ?? 0);
        Total = items.Count == 1 ? ""
            : items.Any(i => i.IsFolder) ? (bytes > 0 ? $"{FileSize.Format(bytes)} of files, and what's in the folders" : "")
            : $"{FileSize.Format(bytes)} in all";
    }

    public IReadOnlyList<PasteItem> Items { get; }

    /// <summary>The thumbnails shown: all of them, up to a dozen.</summary>
    public IReadOnlyList<PasteItem> Shown { get; }

    /// <summary>The one being pasted, shown bigger; null when there are several.</summary>
    public PasteItem? Single => Items.Count == 1 ? Items[0] : null;

    public bool IsSeveral => Items.Count > 1;

    /// <summary>"Do you want to paste these 3 photos at Pixel 7, /sdcard/DCIM/Camera?"</summary>
    public string Question { get; }

    /// <summary>"From This PC, C:\Users\Me\Pictures"</summary>
    public string From { get; }

    /// <summary>How many more there are than are shown.</summary>
    public string More { get; }

    public string Total { get; }

    // "this photo", "these 3 videos", "these 5 items"
    private static string What(IReadOnlyList<PasteItem> items)
    {
        if (items.Count == 1)
            return items[0] switch
            {
                { IsFolder: true } => "this folder",
                { IsPicture: true } => "this photo",
                { IsVideo: true } => "this video",
                _ => "this file",
            };
        var kind = items.All(i => i.IsPicture) ? "photos"
            : items.All(i => i.IsVideo) ? "videos"
            : items.All(i => i.IsFolder) ? "folders"
            : items.Any(i => i.IsFolder) ? "items"
            : "files";
        return $"these {items.Count} {kind}";
    }
}
