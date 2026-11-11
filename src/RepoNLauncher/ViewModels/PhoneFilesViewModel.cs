using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clipboard = System.Windows.Clipboard;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;
using IDataObject = System.Windows.IDataObject;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoNLauncher.Models;
using RepoNLauncher.Services;

namespace RepoNLauncher.ViewModels;

public enum FileSort { Name, Size, Modified }

/// <summary>What the name box under the files is for.</summary>
public enum FileNaming { None, NewFolder, Rename }

/// <summary>How the files are shown: rows with their details, or icons, pictures and videos showing what's in them.</summary>
public enum FileView { Details, Icons }

/// <summary>A shortcut beside the files: a storage, or a folder most files are in.</summary>
public record PhonePlace(string Name, string Path, string Glyph)
{
    public override string ToString() => Name;
}

/// <summary>One folder of the path above the files, to go back to; all but the first come after an arrow.</summary>
public record PathStep(string Name, string Path, bool HasArrow)
{
    public override string ToString() => Name;
}

/// <summary>A file or folder in the Phone files list.</summary>
public partial class PhoneFileItem(DeviceFile file) : ObservableObject
{
    private static readonly HashSet<string> Pictures = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".heif", ".bmp", ".dng", ".nef", ".cr2", ".arw" };
    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".mov", ".webm", ".3gp", ".avi" };
    private static readonly HashSet<string> Sounds = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".m4a", ".aac", ".wav", ".ogg", ".opus", ".flac", ".amr" };
    private static readonly HashSet<string> Packages = new(StringComparer.OrdinalIgnoreCase)
        { ".apk", ".apks", ".xapk", ".zip", ".7z", ".rar", ".tar", ".gz" };
    private static readonly HashSet<string> Documents = new(StringComparer.OrdinalIgnoreCase)
        { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".md", ".csv", ".json", ".xml", ".log" };

    public DeviceFile File { get; } = file;
    public string Name => File.Name;
    public bool IsFolder => File.IsFolder;
    public string Size => File.IsFolder ? "" : FileSize.Format(File.Size);
    public string Modified => DateTimeDisplay.DateAndTime(File.Modified);

    public string Kind => File.IsFolder ? "Folder"
        : Path.GetExtension(File.Name) is { Length: > 1 } extension ? $"{extension[1..].ToUpperInvariant()} file"
        : "File";

    public bool IsPicture => !File.IsFolder && IsPictureName(File.Name);

    public bool IsVideo => !File.IsFolder && IsVideoName(File.Name);

    internal static bool IsPictureName(string name) => Pictures.Contains(Path.GetExtension(name));

    internal static bool IsVideoName(string name) => Videos.Contains(Path.GetExtension(name));

    public bool IsPdf => !File.IsFolder && Path.GetExtension(File.Name).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>What's in the picture or video, for the icon view while it's in view; null shows its icon.</summary>
    [ObservableProperty] private ImageSource? _thumbnail;

    /// <summary>Its Segoe MDL2 Assets icon.</summary>
    public string Glyph => GlyphFor(File.Name, File.IsFolder);

    /// <summary>The Segoe MDL2 Assets icon for a file or folder of this name.</summary>
    internal static string GlyphFor(string name, bool isFolder)
    {
        if (isFolder) return "";
        var extension = Path.GetExtension(name);
        if (Pictures.Contains(extension)) return "";
        if (Videos.Contains(extension)) return "";
        if (Sounds.Contains(extension)) return "";
        if (Packages.Contains(extension)) return "";
        if (Documents.Contains(extension)) return "";
        return "";
    }

    // What screen readers (and UI Automation) call its row.
    public override string ToString() => Name;
}

/// <summary>
/// Files and folders from one folder on a device, copied or dragged from its files window for another device's files
/// (or the same one's) to paste. <see cref="PcFolder"/> is where they've been copied to on this PC, once they have.
/// </summary>
public record DeviceFileSet(string Serial, string Model, string Folder, IReadOnlyList<DeviceFile> Files)
{
    /// <summary>Their format on the clipboard, and in a drag.</summary>
    public const string Format = "RepoNLauncher.DeviceFiles";

    public string? PcFolder { get; init; }

    /// <summary>Puts them in a clipboard or drag's data. They go as bytes: WPF would put a string of its own format in .NET's own serialized form.</summary>
    public void AddTo(DataObject data) => data.SetData(Format, new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this))));

    /// <summary>The device files in a clipboard or drag's data; null when it has none.</summary>
    public static DeviceFileSet? From(IDataObject? data)
    {
        try
        {
            if (data?.GetDataPresent(Format) != true || data.GetData(Format) is not MemoryStream stream) return null;
            // The clipboard can hand back more bytes than were put on it, as zeros.
            return JsonSerializer.Deserialize<DeviceFileSet>(Encoding.UTF8.GetString(stream.ToArray()).TrimEnd('\0'));
        }
        catch (Exception e) when (e is JsonException or ExternalException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The same files from the same device, though one may have been copied to this PC and the other not.</summary>
    public bool IsSameAs(DeviceFileSet other) =>
        Serial == other.Serial && Files.Select(f => f.Path).SequenceEqual(other.Files.Select(f => f.Path));
}

/// <summary>
/// The Phone files window: a device's storage to look through, with files to send to it from this PC, copy from it to
/// this PC or open here, and folders to make, rename and delete. Files can be dropped or pasted on it from File Explorer
/// or another device's files, and dragged or copied from it to those, asking first. One copy at a time, which Stop
/// cancels, removing whatever it left half copied.
/// </summary>
public partial class PhoneFilesViewModel : ObservableObject
{
    public const string InternalStorage = "/sdcard";

    // Where most of a phone's files are, in its internal storage.
    private static readonly (string Name, string Folder, string Glyph)[] CommonFolders =
    {
        ("Download", "Download", ""),
        ("Camera", "DCIM/Camera", ""),
        ("Pictures", "Pictures", ""),
        ("Documents", "Documents", ""),
        ("Movies", "Movies", ""),
        ("Music", "Music", ""),
    };

    // Files opened on this PC (or copied to preview) are copies, kept here a day.
    internal static readonly string OpenedFilesDir = Path.Combine(Path.GetTempPath(), "RepoNLauncher", "Phone files");

    // The next window opens the way the last was left.
    private static FileView _lastView = FileView.Details;
    private static bool _lastShowPreview = true;

    private readonly IDeviceToolsService _tools;
    private readonly List<string> _storages = new() { InternalStorage };
    private List<PhoneFileItem> _all = new();
    private CancellationTokenSource? _copying;
    private bool _copyCanStop;
    private CancellationTokenSource? _preparing; // copying what's on the clipboard to this PC, for File Explorer to paste
    private int _listRequest;
    private string? _pcFolder; // where Save to PC saved last, to start there next time
    private (string Path, bool Select)? _saved; // what Show in folder shows, and whether it's to be selected in its folder
    private PhoneFileItem? _renaming;

    public PhoneFilesViewModel(IDeviceToolsService tools, AndroidDevice device)
    {
        _tools = tools;
        Device = device;
        Thumbnails = new PhoneFileThumbnails(tools, device.Serial);
        Preview = new PhoneFilePreviewViewModel(tools, device.Serial, Thumbnails);
        Places.Add(new PhonePlace("Internal storage", InternalStorage, ""));
        foreach (var (name, folder, glyph) in CommonFolders) Places.Add(new PhonePlace(name, $"{InternalStorage}/{folder}", glyph));
        _pathSteps = StepsTo(InternalStorage);
        _ = Task.Run(RemoveOldOpenedFiles);
    }

    public AndroidDevice Device { get; }

    public string Title => $"Files on {Device.Model}";

    public PhoneFileThumbnails Thumbnails { get; }

    /// <summary>The pane beside the files, showing what's selected.</summary>
    public PhoneFilePreviewViewModel Preview { get; }

    [ObservableProperty] private FileView _view = _lastView;

    [ObservableProperty] private bool _showPreview = _lastShowPreview;

    public ObservableCollection<PhonePlace> Places { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpCommand))]
    private string _folder = InternalStorage;

    [ObservableProperty] private IReadOnlyList<PathStep> _pathSteps;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionText))]
    private IReadOnlyList<PhoneFileItem> _items = Array.Empty<PhoneFileItem>();

    [ObservableProperty] private bool _isListing;

    /// <summary>Over the list when it's empty: why.</summary>
    [ObservableProperty] private string _listNote = string.Empty;

    /// <summary>Copying, renaming, deleting or making a folder: one at a time.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStop), nameof(ShowsProgress))]
    [NotifyCanExecuteChangedFor(nameof(ChooseFilesToSendCommand), nameof(ChooseFolderToSendCommand), nameof(SaveToPcCommand),
        nameof(OpenItemCommand), nameof(DeleteCommand), nameof(StartRenameCommand), nameof(StartNewFolderCommand),
        nameof(ConfirmNameCommand), nameof(PasteCommand))]
    private bool _isBusy;

    /// <summary>
    /// What was copied is being copied to this PC too, so File Explorer can paste it. Other work goes on meanwhile:
    /// pasting it on a device doesn't need it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStop), nameof(ShowsProgress))]
    private bool _isPreparing;

    /// <summary>The work under way is a copy, which Stop can cancel.</summary>
    public bool CanStop => IsBusy && _copyCanStop || IsPreparing;

    public bool ShowsProgress => IsBusy || IsPreparing;

    /// <summary>Asks whether to paste, or drop, what it shows: set by the window, which shows it over itself.</summary>
    public Func<PastePreview, bool>? ConfirmPaste { get; set; }

    [ObservableProperty] private string _status = string.Empty;

    [ObservableProperty] private bool _canShowSaved;

    [ObservableProperty] private bool _showHidden;

    [ObservableProperty] private FileSort _sort = FileSort.Name;

    [ObservableProperty] private bool _sortDescending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionText))]
    [NotifyCanExecuteChangedFor(nameof(SaveToPcCommand), nameof(DeleteCommand), nameof(StartRenameCommand), nameof(CopyCommand))]
    private IReadOnlyList<PhoneFileItem> _selection = Array.Empty<PhoneFileItem>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNaming), nameof(NamingLabel), nameof(NamingAction))]
    private FileNaming _naming;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmNameCommand))]
    private string _nameText = string.Empty;

    public bool IsNaming => Naming != FileNaming.None;

    public string NamingLabel => Naming == FileNaming.Rename ? $"New name for {_renaming?.Name}" : "New folder's name";

    public string NamingAction => Naming == FileNaming.Rename ? "Save name" : "Make folder";

    /// <summary>"24 items", or "3 of 24 selected · 4.5 MB".</summary>
    public string SelectionText
    {
        get
        {
            var all = $"{Items.Count} {(Items.Count == 1 ? "item" : "items")}";
            if (Selection.Count == 0) return all;
            var bytes = Selection.Where(i => !i.IsFolder).Sum(i => i.File.Size);
            return $"{Selection.Count} of {all} selected" + (bytes > 0 ? $" · {FileSize.Format(bytes)}" : "");
        }
    }

    /// <summary>Lists internal storage, and adds any SD card or USB drive to the places.</summary>
    public async Task LoadAsync()
    {
        var listing = OpenFolderAsync(InternalStorage);
        try
        {
            var storages = await _tools.GetStorageFoldersAsync(Device.Serial);
            foreach (var card in storages.Skip(1))
            {
                _storages.Add(card);
                Places.Insert(_storages.Count - 1, new PhonePlace($"SD card {card[(card.LastIndexOf('/') + 1)..]}", card, ""));
            }
        }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException)
        {
            // Internal storage is enough; the listing says if the phone has gone.
        }
        await listing;
    }

    [RelayCommand]
    private Task GoTo(string folder) => OpenFolderAsync(folder);

    private bool CanGoUp() => _storages.All(s => s != Folder);

    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private Task Up() => OpenFolderAsync(ParentOf(Folder));

    [RelayCommand]
    private Task Refresh() => OpenFolderAsync(Folder);

    [RelayCommand]
    private void SortBy(FileSort sort)
    {
        // Size and date go biggest and newest first, the way they're usually wanted.
        SortDescending = Sort == sort ? !SortDescending : sort != FileSort.Name;
        Sort = sort;
        ShowItems();
    }

    partial void OnShowHiddenChanged(bool value) => ShowItems();

    partial void OnViewChanged(FileView value) => _lastView = value;

    // A hidden pane copies nothing from the phone.
    partial void OnShowPreviewChanged(bool value)
    {
        _lastShowPreview = value;
        Preview.Show(value ? Selection : Array.Empty<PhoneFileItem>());
    }

    partial void OnSelectionChanged(IReadOnlyList<PhoneFileItem> value)
    {
        if (ShowPreview) Preview.Show(value);
    }

    private bool CanOpenItem(PhoneFileItem? item) => item != null && (item.IsFolder || !IsBusy);

    /// <summary>Opens a folder; a file is copied to this PC and opened with the app Windows has for it.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenItem))]
    private async Task OpenItemAsync(PhoneFileItem? item)
    {
        if (item == null) return;
        if (item.IsFolder)
        {
            await OpenFolderAsync(item.File.Path);
            return;
        }

        var folder = Path.Combine(OpenedFilesDir, Guid.NewGuid().ToString("N")[..8]);
        var copy = Path.Combine(folder, item.Name);
        var stop = Begin($"Opening {item.Name}…", canStop: true);
        try
        {
            Directory.CreateDirectory(folder);
            await _tools.GetFilesAsync(Device.Serial, new[] { item.File.Path }, folder, stop);
            Status = $"Opened a copy of {item.Name}. Changes to it stay on this PC unless you send it back.";
            try
            {
                Process.Start(new ProcessStartInfo(copy) { UseShellExecute = true });
            }
            catch (Win32Exception)
            {
                // No app opens it: show it in File Explorer instead.
                Process.Start("explorer.exe", $"/select,\"{copy}\"");
            }
        }
        catch (OperationCanceledException)
        {
            TryDeleteOnPc(folder);
            Status = $"Stopped opening {item.Name}.";
        }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException or IOException or UnauthorizedAccessException)
        {
            Status = $"Couldn't open {item.Name}: {e.Message.TrimEnd('.')}.";
        }
        finally
        {
            End();
        }
    }

    [RelayCommand]
    private Task OpenSelected() => Selection.Count == 1 ? OpenItemAsync(Selection[0]) : Task.CompletedTask;

    private bool CanCopy() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanCopy))]
    private async Task ChooseFilesToSendAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = $"Send files to {NameOf(Folder)} on {Device.Model}", Multiselect = true };
        if (dialog.ShowDialog() == true) await SendAsync(dialog.FileNames);
    }

    [RelayCommand(CanExecute = nameof(CanCopy))]
    private async Task ChooseFolderToSendAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = $"Send folders to {NameOf(Folder)} on {Device.Model}", Multiselect = true };
        if (dialog.ShowDialog() == true) await SendAsync(dialog.FolderNames);
    }

    /// <summary>
    /// Copies files and folders from this PC (chosen, dropped on the window or pasted) into a folder on the device: the
    /// one shown, unless another is given.
    /// </summary>
    public async Task SendAsync(IReadOnlyList<string> paths, string? into = null)
    {
        if (paths.Count == 0 || IsBusy) return;
        var folder = into ?? Folder;
        static string NameOnPc(string path) => Path.GetFileName(path.TrimEnd('\\', '/'));

        var names = await NamesInAsync(folder);
        if (IsBusy) return;
        var taken = paths.Where(p => names.Contains(NameOnPc(p))).ToList();
        if (taken.Count > 0)
        {
            var keep = AskToReplace(taken.Select(NameOnPc).ToList(), paths.Count, $"{NameOf(folder)} on {Device.Model}", "Send to the phone");
            if (keep == null) return;
            if (keep == false) paths = paths.Except(taken).ToList();
            if (paths.Count == 0) return;
        }

        var what = paths.Count == 1 ? NameOnPc(paths[0]) : $"{paths.Count} items";
        var added = paths.Except(taken).Select(p => Combine(folder, NameOnPc(p))).ToList();
        var replacing = paths.Count > added.Count;
        var stop = Begin($"Sending {what} to {NameOf(folder)}…", canStop: true);
        try
        {
            var sent = await _tools.SendFilesAsync(Device.Serial, paths, folder, stop);
            Status = $"Sent {what} to {NameOf(folder)}" + (sent == null ? "." : $": {sent.Summary}.");
        }
        catch (OperationCanceledException)
        {
            // What it had started on goes, rather than stay half copied. Files it was replacing are part replaced already.
            await TryDeleteOnPhoneAsync(added);
            Status = $"Stopped sending {what}." + (replacing ? " Files it was replacing may be half copied." : "");
        }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException)
        {
            Status = $"Couldn't send {what}: {e.Message.TrimEnd('.')}.";
        }
        finally
        {
            End();
        }

        if (Folder == folder) await OpenFolderAsync(folder);
    }

    private bool CanChangeSelection() => !IsBusy && Selection.Count > 0;

    [RelayCommand(CanExecute = nameof(CanChangeSelection))]
    private async Task SaveToPcAsync()
    {
        var items = Selection.ToList();
        var what = items.Count == 1 ? items[0].Name : $"{items.Count} items";
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = $"Save {what} from {Device.Model} to",
            InitialDirectory = _pcFolder ?? NativeMethods.DownloadsFolder,
        };
        if (dialog.ShowDialog() != true) return;
        await SaveAsync(items, _pcFolder = dialog.FolderName);
    }

    /// <summary>
    /// Files dragged out of the window and dropped on another program: asks, then copies them into the folder it shows,
    /// when it's File Explorer or the desktop. Null when it's neither.
    /// </summary>
    public async Task DropOnPcAsync(IReadOnlyList<PhoneFileItem> items, string? pcFolder)
    {
        if (items.Count == 0) return;
        if (pcFolder == null)
        {
            Status = "To copy them to this PC, drop them on a folder open in File Explorer, or on the desktop.";
            return;
        }
        if (IsBusy)
        {
            Status = "Wait for what's under way to finish, then drop them again.";
            return;
        }
        var from = $"From {Device.Model}, {ParentOf(items[0].File.Path)}";
        if (!Confirm(new PastePreview(items.Select(PasteItemFor).ToList(), from, "This PC", pcFolder))) return;
        await SaveAsync(items, pcFolder);
    }

    // Copies files and folders from the device into a folder on this PC, asking before replacing any.
    private async Task SaveAsync(IReadOnlyList<PhoneFileItem> items, string target)
    {
        if (IsBusy) return;
        var what = items.Count == 1 ? items[0].Name : $"{items.Count} items";
        var taken = items.Where(i => File.Exists(Path.Combine(target, i.Name)) || Directory.Exists(Path.Combine(target, i.Name))).ToList();
        if (taken.Count > 0)
        {
            var keep = AskToReplace(taken.Select(i => i.Name).ToList(), items.Count,
                Path.GetFileName(target.TrimEnd('\\')) is { Length: > 0 } name ? name : target, "Save to this PC");
            if (keep == null) return;
            if (keep == false) items = items.Except(taken).ToList();
            if (items.Count == 0) return;
            what = items.Count == 1 ? items[0].Name : $"{items.Count} items";
        }

        var added = items.Except(taken).Select(i => Path.Combine(target, i.Name)).ToList();
        var stop = Begin($"Saving {what} to {target}…", canStop: true);
        try
        {
            var copied = await _tools.GetFilesAsync(Device.Serial, items.Select(i => i.File.Path).ToList(), target, stop);
            Status = $"Saved {what} to {target}" + (copied == null ? "." : $": {copied.Summary}.");
            _saved = items.Count == 1 ? (Path.Combine(target, items[0].Name), true) : (target, false);
            CanShowSaved = true;
        }
        catch (OperationCanceledException)
        {
            foreach (var path in added) TryDeleteOnPc(path);
            Status = $"Stopped saving {what}.";
        }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException)
        {
            Status = $"Couldn't save {what}: {e.Message.TrimEnd('.')}.";
        }
        finally
        {
            End();
        }
    }

    [RelayCommand]
    private void ShowSaved()
    {
        if (_saved is not { } saved) return;
        var (path, select) = saved;
        Process.Start("explorer.exe", select ? $"/select,\"{path}\"" : $"\"{path}\"");
    }

    // The copy under way first, then getting what's copied ready for this PC.
    [RelayCommand]
    private void StopCopying()
    {
        if (IsBusy && _copyCanStop) _copying?.Cancel();
        else _preparing?.Cancel();
    }

    /// <summary>
    /// Files and folders from this PC dropped on the window, or pasted, from File Explorer: asks, then sends them to the
    /// folder given.
    /// </summary>
    public async Task PasteFromPcAsync(IReadOnlyList<string> paths, string folder)
    {
        if (paths.Count == 0 || IsBusy) return;
        var parents = paths.Select(p => Path.GetDirectoryName(p.TrimEnd('\\', '/'))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var from = parents is [{ } parent] ? $"From This PC, {parent}" : "From this PC";
        if (!Confirm(new PastePreview(paths.Select(PasteItem.OnPc).ToList(), from, Device.Model, folder))) return;
        await SendAsync(paths, folder);
    }

    /// <summary>
    /// Files and folders from a device dropped on the window, or pasted, from its files window: asks, then copies them to
    /// the folder given. The same device copies them itself; from another, they come through this PC.
    /// </summary>
    public async Task PasteFromDeviceAsync(DeviceFileSet set, string folder)
    {
        if (set.Files.Count == 0 || IsBusy) return;
        var sameDevice = set.Serial == Device.Serial;
        if (sameDevice && set.Files.Any(f => f.IsFolder && (folder == f.Path || folder.StartsWith(f.Path + "/", StringComparison.Ordinal))))
        {
            Status = "A folder can't be pasted inside itself.";
            return;
        }

        var thumbnails = sameDevice ? Thumbnails : new PhoneFileThumbnails(_tools, set.Serial);
        var items = set.Files.Select(f => PasteItem.OnDevice(new PhoneFileItem(f), thumbnails)).ToList();
        if (!Confirm(new PastePreview(items, $"From {set.Model}, {set.Folder}", Device.Model, folder))) return;
        if (sameDevice) await CopyOnDeviceAsync(set, folder);
        else await CopyFromDeviceAsync(set, folder);
    }

    // Copies files to another folder on this device, or as copies beside themselves, named as File Explorer names copies.
    private async Task CopyOnDeviceAsync(DeviceFileSet set, string folder)
    {
        var names = await NamesInAsync(folder);
        if (IsBusy) return;
        var files = set.Files.ToList();
        var beside = set.Folder.TrimEnd('/') == folder.TrimEnd('/');
        var taken = beside ? new List<DeviceFile>() : files.Where(f => names.Contains(f.Name)).ToList();
        if (taken.Count > 0)
        {
            var keep = AskToReplace(taken.Select(f => f.Name).ToList(), files.Count, $"{NameOf(folder)} on {Device.Model}", "Paste");
            if (keep == null) return;
            if (keep == false) files = files.Except(taken).ToList();
            if (files.Count == 0) return;
        }

        var copies = files.Select(f => (From: f.Path, To: Combine(folder, beside ? CopyName(f, names) : f.Name))).ToList();
        var added = copies.Where((_, i) => !taken.Contains(files[i])).Select(c => c.To).ToList();
        var what = files.Count == 1 ? files[0].Name : $"{files.Count} items";
        var stop = Begin($"Copying {what} to {NameOf(folder)}…", canStop: true);
        try
        {
            await _tools.CopyOnDeviceAsync(Device.Serial, copies, stop);
            Status = $"Copied {what} to {NameOf(folder)}.";
        }
        catch (OperationCanceledException)
        {
            await TryDeleteOnPhoneAsync(added);
            Status = $"Stopped copying {what}." + (added.Count < copies.Count ? " Files it was replacing may be half copied." : "");
        }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException)
        {
            Status = $"Couldn't copy {what}: {e.Message.TrimEnd('.')}.";
        }
        finally
        {
            End();
        }

        if (Folder == folder) await OpenFolderAsync(folder);
    }

    // adb can't copy from one device to another, so the files come to this PC on the way, unless they're here already
    // from being copied for File Explorer to paste.
    private async Task CopyFromDeviceAsync(DeviceFileSet set, string folder)
    {
        var names = await NamesInAsync(folder);
        if (IsBusy) return;
        var files = set.Files.ToList();
        var taken = files.Where(f => names.Contains(f.Name)).ToList();
        if (taken.Count > 0)
        {
            var keep = AskToReplace(taken.Select(f => f.Name).ToList(), files.Count, $"{NameOf(folder)} on {Device.Model}", "Paste");
            if (keep == null) return;
            if (keep == false) files = files.Except(taken).ToList();
            if (files.Count == 0) return;
        }

        var what = files.Count == 1 ? files[0].Name : $"{files.Count} items";
        var added = files.Except(taken).Select(f => Combine(folder, f.Name)).ToList();
        var stop = Begin($"Copying {what} from {set.Model} to {NameOf(folder)}…", canStop: true);
        string? onPc = null;
        try
        {
            var copies = set.PcFolder is { } ready && files.All(f => Path.Exists(Path.Combine(ready, f.Name))) ? ready : null;
            if (copies == null)
            {
                copies = onPc = Path.Combine(OpenedFilesDir, Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(onPc);
                await _tools.GetFilesAsync(set.Serial, files.Select(f => f.Path).ToList(), onPc, stop);
            }
            var sent = await _tools.SendFilesAsync(Device.Serial, files.Select(f => Path.Combine(copies, f.Name)).ToList(), folder, stop);
            Status = $"Copied {what} from {set.Model} to {NameOf(folder)}" + (sent == null ? "." : $": {sent.Summary}.");
        }
        catch (OperationCanceledException)
        {
            await TryDeleteOnPhoneAsync(added);
            Status = $"Stopped copying {what}." + (added.Count < files.Count ? " Files it was replacing may be half copied." : "");
        }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException or IOException or UnauthorizedAccessException)
        {
            Status = $"Couldn't copy {what} from {set.Model}: {e.Message.TrimEnd('.')}.";
        }
        finally
        {
            if (onPc != null) TryDeleteOnPc(onPc);
            End();
        }

        if (Folder == folder) await OpenFolderAsync(folder);
    }

    /// <summary>
    /// Copies what's selected, to paste in another folder, another device's files, or File Explorer. Explorer can only
    /// paste files that are on this PC, so they're copied here straight away, which pasting on a device doesn't wait for.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCopySelection))]
    private void Copy()
    {
        var set = new DeviceFileSet(Device.Serial, Device.Model, Folder, Selection.Select(i => i.File).ToList());
        if (!PutOnClipboard(set)) return;
        _ = PrepareForPcAsync(set);
    }

    private bool CanCopySelection() => Selection.Count > 0;

    /// <summary>Pastes what's on the clipboard into the folder shown: files from File Explorer or a device, or a picture.</summary>
    [RelayCommand(CanExecute = nameof(CanCopy))]
    private async Task PasteAsync()
    {
        IDataObject? data;
        try
        {
            data = Clipboard.GetDataObject();
        }
        catch (ExternalException)
        {
            Status = "Couldn't read the clipboard: another app is using it. Try again.";
            return;
        }

        var folder = Folder;
        if (DeviceFileSet.From(data) is { } set) await PasteFromDeviceAsync(set, folder);
        else if (data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths) await PasteFromPcAsync(paths, folder);
        else if (SavePastedPicture(data) is { } picture) await PastePictureAsync(picture, folder);
        else Status = "There's nothing to paste. Copy files, here or in File Explorer, or a picture first.";
    }

    // A picture copied in another app, such as a screenshot, is sent as a PNG.
    private async Task PastePictureAsync(string path, string folder)
    {
        try
        {
            if (Confirm(new PastePreview([PasteItem.OnPc(path)], "From a picture you copied", Device.Model, folder)))
                await SendAsync([path], folder);
        }
        finally
        {
            TryDeleteOnPc(Path.GetDirectoryName(path)!);
        }
    }

    // A picture on the clipboard saved as a PNG on this PC, preferring the PNG snipping tools put there, as the bitmap can
    // lose its see-through parts. Null when there's none.
    private static string? SavePastedPicture(IDataObject? data)
    {
        if (data == null) return null;
        var folder = Path.Combine(OpenedFilesDir, Guid.NewGuid().ToString("N")[..8]);
        var path = Path.Combine(folder, $"Pasted {DateTime.Now:yyyyMMdd-HHmmss}.png");
        try
        {
            if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream png)
            {
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(path, png.ToArray());
                return path;
            }
            if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource image)
            {
                Directory.CreateDirectory(folder);
                using var file = File.Create(path);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                encoder.Save(file);
                return path;
            }
        }
        catch (Exception e) when (e is ExternalException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            TryDeleteOnPc(folder);
        }
        return null;
    }

    // For a device's files to paste and, once they're copied to this PC, File Explorer (and other apps) too.
    private bool PutOnClipboard(DeviceFileSet set)
    {
        var data = new DataObject();
        set.AddTo(data);
        if (set.PcFolder is { } folder)
        {
            var paths = new StringCollection();
            paths.AddRange(set.Files.Select(f => Path.Combine(folder, f.Name)).ToArray());
            data.SetFileDropList(paths);
            // Copied, not moved: the copies here stay for pasting again.
            data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1)));
        }
        try
        {
            Clipboard.SetDataObject(data, copy: true);
            return true;
        }
        catch (ExternalException)
        {
            Status = "Couldn't copy: another app is using the clipboard. Try again.";
            return false;
        }
    }

    // Copies what was copied to this PC, then adds the copies to the clipboard, if it's still what's on it.
    private async Task PrepareForPcAsync(DeviceFileSet set)
    {
        _preparing?.Cancel();
        var preparing = _preparing = new CancellationTokenSource();
        var folder = Path.Combine(OpenedFilesDir, Guid.NewGuid().ToString("N")[..8]);
        var what = set.Files.Count == 1 ? set.Files[0].Name : $"{set.Files.Count} items";
        var it = set.Files.Count == 1 ? "it" : "them";
        IsPreparing = true;
        if (!IsBusy) Status = $"Copied {what}. Getting {it} ready to paste on this PC too…";
        try
        {
            Directory.CreateDirectory(folder);
            await _tools.GetFilesAsync(Device.Serial, set.Files.Select(f => f.Path).ToList(), folder, preparing.Token);
            if (!IsOnClipboard(set))
            {
                TryDeleteOnPc(folder);
                return;
            }
            if (PutOnClipboard(set with { PcFolder = folder }) && !IsBusy)
                Status = $"Copied {what}. Paste {it} in File Explorer, or in a device's files.";
        }
        catch (OperationCanceledException)
        {
            TryDeleteOnPc(folder);
            if (preparing == _preparing && !IsBusy) Status = $"Copied {what}, to paste in a device's files only: stopped getting {it} ready for this PC.";
        }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException or IOException or UnauthorizedAccessException)
        {
            TryDeleteOnPc(folder);
            if (preparing == _preparing && !IsBusy)
                Status = $"Copied {what}, to paste in a device's files. Couldn't get {it} ready to paste on this PC: {e.Message.TrimEnd('.')}.";
        }
        finally
        {
            if (preparing == _preparing)
            {
                _preparing = null;
                IsPreparing = false;
            }
            preparing.Dispose();
        }
    }

    private static bool IsOnClipboard(DeviceFileSet set)
    {
        try
        {
            return DeviceFileSet.From(Clipboard.GetDataObject()) is { } copied && copied.IsSameAs(set);
        }
        catch (ExternalException)
        {
            return false;
        }
    }

    private bool Confirm(PastePreview preview) => ConfirmPaste?.Invoke(preview) ?? true;

    // What the paste window shows of a file here: the picture the preview pane has of it, if it's the one selected.
    private PasteItem PasteItemFor(PhoneFileItem item) =>
        PasteItem.OnDevice(item, Thumbnails, Preview.Item == item ? Preview.Picture : null);

    [RelayCommand(CanExecute = nameof(CanChangeSelection))]
    private async Task DeleteAsync()
    {
        var items = Selection.ToList();
        var question = items.Count == 1
            ? $"Delete {items[0].Name}{(items[0].IsFolder ? " and everything in it" : "")} from {Device.Model}?"
            : $"Delete these {items.Count} items{(items.Any(i => i.IsFolder) ? ", and everything in the folders," : "")} from {Device.Model}?";
        if (MessageBox.Show($"{question}\n\nThis can't be undone: the phone has no recycle bin for them.", "Delete from the phone",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;

        var what = items.Count == 1 ? items[0].Name : $"{items.Count} items";
        Begin($"Deleting {what}…", canStop: false);
        try
        {
            await _tools.DeleteAsync(Device.Serial, items.Select(i => i.File.Path).ToList());
            Status = $"Deleted {what}.";
        }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException)
        {
            Status = $"Couldn't delete {what}: {e.Message.TrimEnd('.')}.";
        }
        finally
        {
            End();
        }
        await OpenFolderAsync(Folder);
    }

    [RelayCommand(CanExecute = nameof(CanCopy))]
    private void StartNewFolder()
    {
        _renaming = null;
        var name = "New folder";
        for (var n = 2; _all.Any(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)); n++) name = $"New folder ({n})";
        NameText = name;
        Naming = FileNaming.NewFolder;
    }

    private bool CanRename() => !IsBusy && Selection.Count == 1;

    [RelayCommand(CanExecute = nameof(CanRename))]
    private void StartRename()
    {
        _renaming = Selection[0];
        NameText = _renaming.Name;
        Naming = FileNaming.None; // so the label follows the item even when renaming another straight after
        Naming = FileNaming.Rename;
    }

    [RelayCommand]
    private void CancelName() => Naming = FileNaming.None;

    private bool CanConfirmName() => !IsBusy && IsGoodName(NameText.Trim());

    [RelayCommand(CanExecute = nameof(CanConfirmName))]
    private async Task ConfirmNameAsync()
    {
        var name = NameText.Trim();
        var naming = Naming;
        var item = _renaming;
        Naming = FileNaming.None;
        if (naming == FileNaming.Rename && (item == null || item.Name == name)) return;

        Begin(naming == FileNaming.Rename ? $"Renaming {item!.Name}…" : $"Making the folder {name}…", canStop: false);
        try
        {
            if (naming == FileNaming.Rename)
            {
                await _tools.RenameAsync(Device.Serial, item!.File.Path, name);
                Status = $"Renamed {item.Name} to {name}.";
            }
            else
            {
                await _tools.CreateFolderAsync(Device.Serial, Combine(Folder, name));
                Status = $"Made the folder {name}.";
            }
        }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException)
        {
            Status = (naming == FileNaming.Rename ? $"Couldn't rename {item!.Name}: " : $"Couldn't make the folder {name}: ")
                     + $"{e.Message.TrimEnd('.')}.";
        }
        finally
        {
            End();
        }
        await OpenFolderAsync(Folder);
    }

    /// <summary>Stops a copy in progress, and the thumbnails and preview being made, for the window closing.</summary>
    public void Close()
    {
        _copying?.Cancel();
        _preparing?.Cancel();
        Thumbnails.Close();
        Preview.Close();
    }

    private async Task OpenFolderAsync(string folder)
    {
        var request = ++_listRequest;
        IsListing = true;
        try
        {
            var files = await _tools.ListFolderAsync(Device.Serial, folder);
            if (request != _listRequest) return;
            Folder = folder;
            PathSteps = StepsTo(folder);
            _all = files.Select(f => new PhoneFileItem(f)).ToList();
            ShowItems();
        }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException)
        {
            if (request != _listRequest) return;
            Status = $"Couldn't open {NameOf(folder)}: {e.Message.TrimEnd('.')}.";
            if (Items.Count == 0) ListNote = Status;
        }
        finally
        {
            if (request == _listRequest) IsListing = false;
        }
    }

    // Folders first, then in the order picked; the same names or sizes in name order.
    private void ShowItems()
    {
        var shown = ShowHidden ? _all : _all.Where(i => !i.Name.StartsWith('.')).ToList();
        var folders = shown.OrderBy(i => i.IsFolder ? 0 : 1);
        var sorted = Sort switch
        {
            FileSort.Size => SortDescending ? folders.ThenByDescending(i => i.File.Size) : folders.ThenBy(i => i.File.Size),
            FileSort.Modified => SortDescending ? folders.ThenByDescending(i => i.File.Modified) : folders.ThenBy(i => i.File.Modified),
            _ => SortDescending
                ? folders.ThenByDescending(i => i.Name, NativeMethods.ExplorerNameOrder)
                : folders.ThenBy(i => i.Name, NativeMethods.ExplorerNameOrder),
        };
        Items = sorted.ThenBy(i => i.Name, NativeMethods.ExplorerNameOrder).ToList();
        Selection = Array.Empty<PhoneFileItem>();
        ListNote = Items.Count > 0 ? string.Empty
            : _all.Count > 0 ? "Only hidden files are in here. Tick Hidden files to see them."
            : "This folder is empty. Drop or paste files here to send them to it.";
    }

    private CancellationToken Begin(string status, bool canStop)
    {
        _copyCanStop = canStop;
        IsBusy = true;
        CanShowSaved = false;
        Status = status;
        _copying = new CancellationTokenSource();
        return _copying.Token;
    }

    private void End()
    {
        IsBusy = false;
        _copying?.Dispose();
        _copying = null;
    }

    // The names in a folder, to ask before replacing any. The phone's storage ignores capitals, so "Photo.jpg" would
    // replace "photo.jpg". None when it can't be listed, which copying to it will say.
    private async Task<HashSet<string>> NamesInAsync(string folder)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var files = folder == Folder ? _all.Select(i => i.File) : await _tools.ListFolderAsync(Device.Serial, folder);
            names.UnionWith(files.Select(f => f.Name));
        }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException)
        {
            // the copy says what's wrong
        }
        return names;
    }

    // "photo.jpg" → "photo - Copy.jpg", then "photo - Copy (2).jpg", as File Explorer names copies, taking the name.
    private static string CopyName(DeviceFile file, HashSet<string> taken)
    {
        var extension = file.IsFolder ? "" : Path.GetExtension(file.Name);
        if (extension.Length == file.Name.Length) extension = ""; // ".nomedia" is a name, not an extension
        var stem = file.Name[..^extension.Length];
        var name = $"{stem} - Copy{extension}";
        for (var n = 2; taken.Contains(name); n++) name = $"{stem} - Copy ({n}){extension}";
        taken.Add(name);
        return name;
    }

    /// <summary>
    /// Of the <paramref name="total"/> being copied, these have names already in use there: replace them (true), copy
    /// only the others (false), or copy nothing (null).
    /// </summary>
    private static bool? AskToReplace(IReadOnlyList<string> names, int total, string where, string title)
    {
        var listed = names.Count <= 5 ? string.Join("\n", names) : string.Join("\n", names.Take(4)) + $"\nand {names.Count - 4} more";
        var them = names.Count == 1 ? "it" : "them";
        var others = total - names.Count;
        var question = others == 0
            ? $"Replace {them}?"
            : $"Replace {them}? No copies only the other {(others == 1 ? "one" : others.ToString(CultureInfo.CurrentCulture))}.";
        var answer = MessageBox.Show($"{(names.Count == 1 ? "This is" : "These are")} in {where} already:\n\n{listed}\n\n{question}",
            title, others == 0 ? MessageBoxButton.YesNo : MessageBoxButton.YesNoCancel, MessageBoxImage.Question,
            others == 0 ? MessageBoxResult.No : MessageBoxResult.Cancel);
        return answer switch
        {
            MessageBoxResult.Yes => true,
            MessageBoxResult.No when others > 0 => false,
            _ => null,
        };
    }

    private async Task TryDeleteOnPhoneAsync(IReadOnlyList<string> paths)
    {
        try
        {
            await _tools.DeleteAsync(Device.Serial, paths);
        }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException)
        {
            // unplugged, most likely
        }
    }

    internal static void TryDeleteOnPc(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // in use: it stays, as a part copy
        }
    }

    private static void RemoveOldOpenedFiles()
    {
        try
        {
            if (!Directory.Exists(OpenedFilesDir)) return;
            foreach (var folder in new DirectoryInfo(OpenedFilesDir).EnumerateDirectories())
                if (folder.CreationTimeUtc < DateTime.UtcNow.AddDays(-1)) TryDeleteOnPc(folder.FullName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // tried again next time
        }
    }

    // A name Android can give a file: not empty, no slashes, and not one of the names meaning "this folder" or "the one up".
    private static bool IsGoodName(string name) => name.Length is > 0 and <= 255 && name is not "." and not ".." && !name.Contains('/');

    /// <summary>"Internal storage" or "SD card 1A2B-3C4D" for a storage, otherwise the folder's own name.</summary>
    private string NameOf(string folder)
    {
        if (_storages.Contains(folder)) return Places.FirstOrDefault(p => p.Path == folder)?.Name ?? folder;
        return folder[(folder.TrimEnd('/').LastIndexOf('/') + 1)..];
    }

    private IReadOnlyList<PathStep> StepsTo(string folder)
    {
        var storage = _storages.Where(s => folder == s || folder.StartsWith(s + "/", StringComparison.Ordinal))
            .OrderByDescending(s => s.Length).FirstOrDefault() ?? "";
        var steps = new List<PathStep>();
        if (storage.Length > 0) steps.Add(new PathStep(NameOf(storage), storage, false));
        var path = storage;
        foreach (var part in folder[storage.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            path += "/" + part;
            steps.Add(new PathStep(part, path, steps.Count > 0));
        }
        return steps;
    }

    private static string Combine(string folder, string name) => folder.TrimEnd('/') + "/" + name;

    private static string ParentOf(string folder)
    {
        var slash = folder.TrimEnd('/').LastIndexOf('/');
        return slash <= 0 ? "/" : folder[..slash];
    }
}
