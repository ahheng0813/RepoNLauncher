using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
public class PhoneFileItem(DeviceFile file)
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
    public string Modified => File.Modified.ToString("g", CultureInfo.CurrentCulture);

    public string Kind => File.IsFolder ? "Folder"
        : Path.GetExtension(File.Name) is { Length: > 1 } extension ? $"{extension[1..].ToUpperInvariant()} file"
        : "File";

    /// <summary>Its Segoe MDL2 Assets icon.</summary>
    public string Glyph
    {
        get
        {
            if (File.IsFolder) return "";
            var extension = Path.GetExtension(File.Name);
            if (Pictures.Contains(extension)) return "";
            if (Videos.Contains(extension)) return "";
            if (Sounds.Contains(extension)) return "";
            if (Packages.Contains(extension)) return "";
            if (Documents.Contains(extension)) return "";
            return "";
        }
    }

    // What screen readers (and UI Automation) call its row.
    public override string ToString() => Name;
}

/// <summary>
/// The Phone files window: a device's storage to look through, with files to send to it from this PC, copy from it to
/// this PC or open here, and folders to make, rename and delete. One copy at a time, which Stop cancels, removing
/// whatever it left half copied.
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

    // Files opened on this PC are copies, kept here a day.
    private static readonly string OpenedFilesDir = Path.Combine(Path.GetTempPath(), "RepoNLauncher", "Phone files");

    private readonly IDeviceToolsService _tools;
    private readonly List<string> _storages = new() { InternalStorage };
    private List<PhoneFileItem> _all = new();
    private CancellationTokenSource? _copying;
    private int _listRequest;
    private string? _pcFolder; // where Save to PC saved last, to start there next time
    private string? _saved; // what Show in folder shows
    private PhoneFileItem? _renaming;

    public PhoneFilesViewModel(IDeviceToolsService tools, AndroidDevice device)
    {
        _tools = tools;
        Device = device;
        Places.Add(new PhonePlace("Internal storage", InternalStorage, ""));
        foreach (var (name, folder, glyph) in CommonFolders) Places.Add(new PhonePlace(name, $"{InternalStorage}/{folder}", glyph));
        _pathSteps = StepsTo(InternalStorage);
        _ = Task.Run(RemoveOldOpenedFiles);
    }

    public AndroidDevice Device { get; }

    public string Title => $"Files on {Device.Model}";

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
    [NotifyCanExecuteChangedFor(nameof(ChooseFilesToSendCommand), nameof(ChooseFolderToSendCommand), nameof(SaveToPcCommand),
        nameof(OpenItemCommand), nameof(DeleteCommand), nameof(StartRenameCommand), nameof(StartNewFolderCommand),
        nameof(ConfirmNameCommand))]
    private bool _isBusy;

    /// <summary>The work under way is a copy, which Stop can cancel.</summary>
    [ObservableProperty] private bool _canStop;

    [ObservableProperty] private string _status = string.Empty;

    [ObservableProperty] private bool _canShowSaved;

    [ObservableProperty] private bool _showHidden;

    [ObservableProperty] private FileSort _sort = FileSort.Name;

    [ObservableProperty] private bool _sortDescending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionText))]
    [NotifyCanExecuteChangedFor(nameof(SaveToPcCommand), nameof(DeleteCommand), nameof(StartRenameCommand))]
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

    /// <summary>Copies files and folders from this PC (chosen, or dropped on the window) into the folder shown.</summary>
    public async Task SendAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || IsBusy) return;
        var folder = Folder;
        static string NameOnPc(string path) => Path.GetFileName(path.TrimEnd('\\', '/'));

        // The phone's storage ignores capitals, so "Photo.jpg" would replace "photo.jpg".
        var taken = paths.Where(p => _all.Any(i => string.Equals(i.Name, NameOnPc(p), StringComparison.OrdinalIgnoreCase))).ToList();
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
        var target = _pcFolder = dialog.FolderName;

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
            _saved = items.Count == 1 ? Path.Combine(target, items[0].Name) : target;
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
        if (_saved == null) return;
        Process.Start("explorer.exe", File.Exists(_saved) || Directory.Exists(_saved) && _saved != _pcFolder
            ? $"/select,\"{_saved}\""
            : $"\"{_saved}\"");
    }

    [RelayCommand]
    private void StopCopying() => _copying?.Cancel();

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

    /// <summary>Stops a copy in progress, for the window closing.</summary>
    public void Close() => _copying?.Cancel();

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
            : "This folder is empty. Drop files here to send them to it.";
    }

    private CancellationToken Begin(string status, bool canStop)
    {
        IsBusy = true;
        CanStop = canStop;
        CanShowSaved = false;
        Status = status;
        _copying = new CancellationTokenSource();
        return _copying.Token;
    }

    private void End()
    {
        IsBusy = false;
        CanStop = false;
        _copying?.Dispose();
        _copying = null;
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

    private static void TryDeleteOnPc(string path)
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
