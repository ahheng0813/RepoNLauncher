using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoNLauncher.Services;

namespace RepoNLauncher.ViewModels;

public enum PreviewKind { Nothing, Several, Folder, Picture, Video, Pdf, Other }

/// <summary>
/// The pane beside the Phone files: the picture selected, a video's thumbnail to play it from, or a PDF, copied from the
/// phone once the selection settles. Anything else shows what it is.
/// </summary>
public partial class PhoneFilePreviewViewModel(IDeviceToolsService tools, string serial, PhoneFileThumbnails thumbnails) : ObservableObject
{
    // As big as the pane might show a picture, on a 4K screen.
    private const int PictureSize = 2048;
    private const long WholePictureLimit = 100L * 1024 * 1024;
    // PDFs up to this big are copied to show as soon as they're selected; bigger ones, and videos, when they're asked for.
    private const long PdfLimit = 50L * 1024 * 1024;
    // Not while the selection runs down the list under the arrow keys.
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(150);
    // What the pane's browser (Edge) plays; the rest open in an app on this PC.
    private static readonly HashSet<string> Playable = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov", ".webm", ".mkv" };

    // Phone file → its copy on this PC, so one previewed again isn't copied again.
    private readonly Dictionary<string, string> _copies = new();
    private CancellationTokenSource? _loading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPlay), nameof(CanShowPdf))]
    private PreviewKind _kind;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpen))]
    private PhoneFileItem? _item;

    /// <summary>Its name, or how many are selected.</summary>
    [ObservableProperty] private string _title = string.Empty;

    /// <summary>"JPG file · 3.4 MB · 4000 × 3000".</summary>
    [ObservableProperty] private string _details = string.Empty;

    [ObservableProperty] private string _modified = string.Empty;

    /// <summary>The icon shown when there's no picture.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsIcon))]
    private string _glyph = string.Empty;

    /// <summary>The picture, or the video's thumbnail.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsIcon))]
    private ImageSource? _picture;

    [ObservableProperty] private bool _isLoading;

    /// <summary>Under the preview: what it's doing, or why there's not more to see.</summary>
    [ObservableProperty] private string _note = string.Empty;

    /// <summary>A copy on this PC of the video or PDF, for the pane's browser to show.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsIcon))]
    private string? _mediaFile;

    /// <summary>A video that can be played here, or a PDF too big to copy unasked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPlay), nameof(CanShowPdf))]
    [NotifyCanExecuteChangedFor(nameof(LoadMediaCommand))]
    private bool _canLoadMedia;

    public bool CanPlay => CanLoadMedia && Kind == PreviewKind.Video;

    public bool CanShowPdf => CanLoadMedia && Kind == PreviewKind.Pdf;

    public bool ShowsIcon => Picture == null && MediaFile == null && Glyph.Length > 0;

    /// <summary>A file, which can be opened in an app on this PC.</summary>
    public bool CanOpen => Item is { IsFolder: false };

    public void Show(IReadOnlyList<PhoneFileItem> selection)
    {
        var item = selection.Count == 1 ? selection[0] : null;
        if (item != null && item == Item) return;

        _loading?.Cancel();
        _loading = null;
        Item = item;
        Picture = null;
        MediaFile = null;
        CanLoadMedia = false;
        IsLoading = false;
        Note = string.Empty;

        if (item == null)
        {
            Kind = selection.Count == 0 ? PreviewKind.Nothing : PreviewKind.Several;
            Title = selection.Count == 0 ? string.Empty : $"{selection.Count} items selected";
            var bytes = selection.Where(i => !i.IsFolder).Sum(i => i.File.Size);
            Details = bytes > 0 ? FileSize.Format(bytes) : string.Empty;
            Modified = string.Empty;
            Glyph = selection.Count == 0 ? string.Empty : "";
            return;
        }

        Kind = item.IsFolder ? PreviewKind.Folder
            : item.IsPicture ? PreviewKind.Picture
            : item.IsVideo ? PreviewKind.Video
            : item.IsPdf ? PreviewKind.Pdf
            : PreviewKind.Other;
        Title = item.Name;
        Details = item.IsFolder ? item.Kind : $"{item.Kind} · {item.Size}";
        Modified = $"Modified {item.Modified}";
        Glyph = item.Glyph;
        if (Kind is PreviewKind.Picture or PreviewKind.Video or PreviewKind.Pdf)
        {
            _loading = new CancellationTokenSource();
            _ = LoadAsync(item, Kind, _loading.Token);
        }
    }

    /// <summary>The pane's browser couldn't show the copy.</summary>
    public void MediaFailed(string why)
    {
        MediaFile = null;
        Note = why;
    }

    public void Close() => _loading?.Cancel();

    private async Task LoadAsync(PhoneFileItem item, PreviewKind kind, CancellationToken ct)
    {
        // The icon view's small copy straight away, if it made one.
        if (thumbnails.Kept(item) is { } small) Picture = PhonePictures.Decode(small, PhoneFileThumbnails.Size, orientation: 1)?.Image;
        CanLoadMedia = kind == PreviewKind.Video && Playable.Contains(Path.GetExtension(item.Name));
        IsLoading = true;
        try
        {
            await Task.Delay(Settle, ct);
            switch (kind)
            {
                case PreviewKind.Picture:
                    await LoadPictureAsync(item, ct);
                    break;
                case PreviewKind.Video:
                    if (!CanLoadMedia) Note = "Double-click it to play it in an app on this PC.";
                    await LoadGalleryCopyAsync(item, ct);
                    break;
                case PreviewKind.Pdf when item.File.Size <= PdfLimit:
                    await CopyMediaAsync(item, ct);
                    break;
                case PreviewKind.Pdf:
                    CanLoadMedia = true;
                    Note = $"It's {item.Size}, so it's copied from the phone to preview only when you ask.";
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // something else was selected
        }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException or IOException or UnauthorizedAccessException)
        {
            if (!ct.IsCancellationRequested) Note = $"Couldn't preview it: {e.Message.TrimEnd('.')}.";
        }
        finally
        {
            if (!ct.IsCancellationRequested) IsLoading = false;
        }
    }

    private async Task LoadPictureAsync(PhoneFileItem item, CancellationToken ct)
    {
        var extension = Path.GetExtension(item.Name).ToUpperInvariant();
        if (item.File.Size <= WholePictureLimit)
        {
            using var data = new MemoryStream((int)item.File.Size);
            await tools.ReadFileAsync(serial, item.File.Path, data, null, ct);
            var whole = data.ToArray();
            var picture = await Task.Run(() => PhonePictures.Decode(whole, PictureSize), ct);
            ct.ThrowIfCancellationRequested();
            if (picture != null)
            {
                Picture = picture.Image;
                Details = $"{item.Kind} · {item.Size} · {picture.Width} × {picture.Height}";
                return;
            }
            Note = $"This PC can't read {extension} pictures, so this is the phone gallery's small copy.";
        }
        else
        {
            Note = "It's too big to preview whole, so this is the phone gallery's small copy. Double-click it to open it.";
        }

        if (!await LoadGalleryCopyAsync(item, ct))
            Note = item.File.Size <= WholePictureLimit
                ? $"This PC can't read {extension} pictures, and the phone's gallery has no copy of it."
                : "It's too big to preview, and the phone's gallery has no copy of it. Double-click it to open it.";
    }

    // The small copy the phone's gallery keeps: a video's thumbnail, or a picture this PC can't read. Whether there was one.
    private async Task<bool> LoadGalleryCopyAsync(PhoneFileItem item, CancellationToken ct)
    {
        var copy = await tools.GetMediaThumbnailAsync(serial, item.File.Path, item.IsVideo, ct);
        var picture = copy == null ? null : await Task.Run(() => PhonePictures.Decode(copy, PictureSize), ct);
        ct.ThrowIfCancellationRequested();
        if (picture == null) return Picture != null;
        Picture = picture.Image;
        return true;
    }

    /// <summary>Copies the video (to play) or PDF from the phone to show it in the pane.</summary>
    [RelayCommand(CanExecute = nameof(CanLoadMedia))]
    private async Task LoadMediaAsync()
    {
        if (Item is not { } item || _loading is not { } loading) return;
        CanLoadMedia = false;
        IsLoading = true;
        try
        {
            await CopyMediaAsync(item, loading.Token);
        }
        catch (OperationCanceledException)
        {
            // something else was selected
        }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException or IOException or UnauthorizedAccessException)
        {
            if (loading.IsCancellationRequested) return;
            Note = $"Couldn't copy it: {e.Message.TrimEnd('.')}.";
            CanLoadMedia = true;
        }
        finally
        {
            if (!loading.IsCancellationRequested) IsLoading = false;
        }
    }

    private async Task CopyMediaAsync(PhoneFileItem item, CancellationToken ct)
    {
        var key = $"{item.File.Path}|{item.File.Size}|{item.File.Modified.Ticks}";
        if (!_copies.TryGetValue(key, out var copy) || !File.Exists(copy))
        {
            var folder = Path.Combine(PhoneFilesViewModel.OpenedFilesDir, Guid.NewGuid().ToString("N")[..8]);
            copy = Path.Combine(folder, item.Name);
            Note = "Copying it from the phone…";
            // Progress comes a moment after it's reported, so the last can come after the copy is done.
            var copying = true;
            var progress = new Progress<long>(bytes =>
            {
                if (copying && !ct.IsCancellationRequested && item.File.Size > 0)
                    Note = $"Copying it from the phone… {Math.Min(99, bytes * 100 / item.File.Size)}%";
            });
            try
            {
                Directory.CreateDirectory(folder);
                long copied;
                await using (var file = File.Create(copy)) copied = await tools.ReadFileAsync(serial, item.File.Path, file, progress, ct);
                if (copied == 0 && item.File.Size > 0) throw new InvalidOperationException("the phone didn't let it be read");
            }
            catch
            {
                PhoneFilesViewModel.TryDeleteOnPc(folder);
                throw;
            }
            finally
            {
                copying = false;
            }
            _copies[key] = copy;
        }

        ct.ThrowIfCancellationRequested();
        Note = string.Empty;
        MediaFile = copy;
    }
}
