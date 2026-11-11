using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoNLauncher.Models;
using RepoNLauncher.Services;
using MessageBox = System.Windows.MessageBox;

namespace RepoNLauncher.ViewModels;

/// <summary>An emulator in the Android emulators list, in its window and in Settings.</summary>
public partial class EmulatorItemViewModel : ObservableObject
{
    public EmulatorItemViewModel(AndroidEmulator emulator) => Update(emulator);

    /// <summary>The AVD name, which starts it and never changes.</summary>
    public string Name { get; private set; } = string.Empty;

    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private string _details = string.Empty;

    internal void Update(AndroidEmulator emulator)
    {
        Name = emulator.Name;
        DisplayName = emulator.DisplayName;
        Details = emulator.Details;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart), nameof(CanStop), nameof(CanDelete), nameof(IsShownRunning))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart), nameof(CanDelete), nameof(CanRename), nameof(IsBusy))]
    private bool _isStarting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart), nameof(CanStop), nameof(CanDelete), nameof(CanRename), nameof(IsShownRunning), nameof(IsBusy))]
    private bool _isStopping;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart), nameof(CanDelete), nameof(CanRename), nameof(IsBusy))]
    private bool _isDeleting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart), nameof(CanStop), nameof(CanDelete), nameof(CanRename), nameof(IsShownRunning), nameof(IsNotRenaming))]
    private bool _isRenaming;

    /// <summary>The name being typed while it's renamed.</summary>
    [ObservableProperty] private string _newName = string.Empty;
    [ObservableProperty] private string? _renameError;

    partial void OnNewNameChanged(string value) => RenameError = null;

    public bool IsBusy => IsStarting || IsStopping || IsDeleting;
    public bool IsNotRenaming => !IsRenaming;
    public bool CanStart => !IsRunning && !IsBusy && !IsRenaming;
    public bool CanStop => IsRunning && !IsStopping && !IsRenaming;
    public bool IsShownRunning => IsRunning && !IsStopping && !IsRenaming;
    public bool CanRename => !IsBusy && !IsRenaming;

    /// <summary>Running, it holds its files open: deleting half of them would leave it broken.</summary>
    public bool CanDelete => !IsRunning && !IsBusy && !IsRenaming;
}

/// <summary>
/// The Android emulators on this PC, in their own window and in Settings: each can be started, shut down, renamed and
/// deleted, and new ones made from an installed system image. It lives as long as the dashboard, so an emulator that's
/// starting finishes with both closed. The Phone Screen starts emulators through it too, so all three see the same start.
/// </summary>
public partial class EmulatorsViewModel : ObservableObject
{
    private readonly IEmulatorService _emulators;
    // Starts still going, by AVD name: a refresh, Start and the Phone Screen's Show all see them.
    private readonly Dictionary<string, Task> _starting = new(StringComparer.OrdinalIgnoreCase);
    private bool _suggestingName;
    private bool _nameEdited;

    public EmulatorsViewModel(IEmulatorService emulators) => _emulators = emulators;

    public ObservableCollection<EmulatorItemViewModel> Emulators { get; } = new();
    public ObservableCollection<SystemImage> Images { get; } = new();
    public IReadOnlyList<EmulatorDevice> Devices => EmulatorDevice.All;

    /// <summary>
    /// Raised when an emulator has started or shut down, or been created, renamed or deleted, so the device lists can
    /// catch up.
    /// </summary>
    public event Action? Changed;

    /// <summary>What's missing for emulators, if anything: the SDK or the emulator itself.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseEmulators))]
    private string _setupNote = string.Empty;

    public bool CanUseEmulators => SetupNote.Length == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImages))]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private SystemImage? _selectedImage;

    public bool HasImages => Images.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private EmulatorDevice _selectedDevice = EmulatorDevice.All[1];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameError))]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private string _newName = string.Empty;

    [ObservableProperty] private bool _startAfterCreate = true;
    [ObservableProperty] private string _status = string.Empty;

    /// <summary>Why the name can't be used, shown under it.</summary>
    public string NameError
    {
        get
        {
            var name = _emulators.NameFor(NewName);
            if (NewName.Trim().Length == 0) return string.Empty;
            if (name.Length == 0) return "Use letters or digits in the name.";
            if (_emulators.Exists(name)) return $"There's already an emulator called {name}.";
            return IsNameTaken(NewName.Trim(), except: null) ? $"There's already an emulator called {NewName.Trim()}." : string.Empty;
        }
    }

    /// <summary>An emulator to start is starting, from here or the Phone Screen.</summary>
    public bool IsStarting(string name) => _starting.ContainsKey(name);

    /// <summary>Reads the emulators and images again, and which emulators are running. A row being renamed stays as it is.</summary>
    public async Task RefreshAsync()
    {
        if (_emulators.SdkRoot == null)
        {
            SetupNote = "The Android SDK isn't on this PC. Install Android Studio, which sets it up, then open this window again.";
            return;
        }
        if (!_emulators.HasEmulator)
        {
            SetupNote = "The Android Emulator isn't installed. In Android Studio, open SDK Manager, then SDK Tools, and tick Android Emulator.";
            return;
        }
        SetupNote = string.Empty;

        // In place, by AVD name, so a row being renamed or shut down keeps what it's doing.
        var found = _emulators.GetEmulators();
        foreach (var gone in Emulators.Where(item => !found.Any(e => SameName(e.Name, item.Name))).ToList()) Emulators.Remove(gone);
        for (var i = 0; i < found.Count; i++)
        {
            var item = Emulators.FirstOrDefault(e => SameName(e.Name, found[i].Name));
            if (item == null)
            {
                Emulators.Insert(i, item = new EmulatorItemViewModel(found[i]));
            }
            else
            {
                item.Update(found[i]);
                var at = Emulators.IndexOf(item);
                if (at != i) Emulators.Move(at, i);
            }
            item.IsStarting = IsStarting(item.Name);
        }

        var keep = SelectedImage?.RelativePath;
        Images.Clear();
        foreach (var image in _emulators.GetSystemImages()) Images.Add(image);
        SelectedImage = Images.FirstOrDefault(i => i.RelativePath == keep) ?? Images.FirstOrDefault();
        OnPropertyChanged(nameof(HasImages));
        OnPropertyChanged(nameof(NameError));
        SuggestName();

        var running = await _emulators.GetRunningAsync();
        foreach (var item in Emulators) item.IsRunning = running.ContainsKey(item.Name);
    }

    partial void OnSelectedDeviceChanged(EmulatorDevice value) => SuggestName();
    partial void OnSelectedImageChanged(SystemImage? value) => SuggestName();

    partial void OnNewNameChanged(string value)
    {
        if (!_suggestingName) _nameEdited = value.Trim().Length > 0;
    }

    // Like Android Studio: the device and Android version, until a name is typed. "Medium Phone API 36.1", then "… 2".
    private void SuggestName()
    {
        if (_nameEdited || SelectedImage == null) return;
        var baseName = $"{SelectedDevice.Name} API {SelectedImage.ApiLevel}";
        var name = baseName;
        for (var n = 2; _emulators.Exists(_emulators.NameFor(name)) || IsNameTaken(name, except: null); n++) name = $"{baseName} {n}";

        _suggestingName = true;
        NewName = name;
        _suggestingName = false;
    }

    // Two emulators by one name are the trouble the device lists show emulators' own names to avoid.
    private bool IsNameTaken(string displayName, EmulatorItemViewModel? except) =>
        Emulators.Any(e => e != except && e.DisplayName.Equals(displayName, StringComparison.CurrentCultureIgnoreCase));

    private static bool SameName(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    private bool CanCreate() => SelectedImage != null && _emulators.NameFor(NewName).Length > 0 && NameError.Length == 0;

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        var displayName = NewName.Trim();
        try
        {
            _emulators.Create(displayName, SelectedDevice, SelectedImage!);
        }
        catch (Exception e)
        {
            Status = $"Couldn't create {displayName}: {e.Message}";
            return;
        }

        _nameEdited = false;
        await RefreshAsync();
        Status = $"Created {displayName}.";
        Changed?.Invoke();
        var created = Emulators.FirstOrDefault(e => e.Name == _emulators.NameFor(displayName));
        if (StartAfterCreate && created != null) _ = StartQuietlyAsync(created.Name);
    }

    [RelayCommand]
    private void Start(EmulatorItemViewModel item)
    {
        if (item.CanStart) _ = StartQuietlyAsync(item.Name);
    }

    /// <summary>
    /// Starts it in its own window, as its Start button does, and finishes once Android has booted. A start already going
    /// is joined, not begun again. Throws with why when it doesn't start; Status says so too.
    /// </summary>
    public Task StartAsync(string name, IProgress<string>? progress = null)
    {
        if (_starting.TryGetValue(name, out var starting)) return starting;
        var start = RunStartAsync(name, progress);
        // One that failed straight away is over already.
        if (!start.IsCompleted) _starting[name] = start;
        return start;
    }

    private async Task RunStartAsync(string name, IProgress<string>? progress)
    {
        var displayName = Emulators.FirstOrDefault(e => SameName(e.Name, name))?.DisplayName
                          ?? _emulators.GetEmulators().FirstOrDefault(e => SameName(e.Name, name))?.DisplayName
                          ?? name.Replace('_', ' ');
        SetStarting(name, true);
        var report = new Progress<string>(step =>
        {
            Status = $"{displayName}: {step} The first start of a new emulator takes a few minutes.";
            progress?.Report(step);
        });
        try
        {
            await _emulators.StartAsync(name, report);
            Status = $"{displayName} is running. It's in the Phone Screen's device list, and Flutter's.";
        }
        catch (Exception e)
        {
            Status = $"{displayName} didn't start: {e.Message}";
            throw;
        }
        finally
        {
            _starting.Remove(name);
            SetStarting(name, false);
            Changed?.Invoke();
            _ = RefreshAsync();
        }
    }

    private void SetStarting(string name, bool starting)
    {
        foreach (var row in Emulators.Where(e => SameName(e.Name, name))) row.IsStarting = starting;
    }

    // Start's buttons say how it went in Status, so there's nothing more to do with a failure here.
    private async Task StartQuietlyAsync(string name)
    {
        try
        {
            await StartAsync(name);
        }
        catch (Exception)
        {
            // In Status already.
        }
    }

    /// <summary>Shuts a running emulator down, as closing its window does.</summary>
    [RelayCommand]
    private void ShutDown(EmulatorItemViewModel item) => _ = ShutDownAsync(item);

    private async Task ShutDownAsync(EmulatorItemViewModel item)
    {
        if (!item.CanStop) return;
        item.IsStopping = true;
        Status = $"Shutting down {item.DisplayName}…";
        try
        {
            await _emulators.StopAsync(item.Name);
            item.IsRunning = false;
            Status = $"{item.DisplayName} is shut down.";
        }
        catch (Exception e)
        {
            Status = $"{item.DisplayName} didn't shut down: {e.Message}";
        }
        finally
        {
            item.IsStopping = false;
        }
        Changed?.Invoke();
        await RefreshAsync();
    }

    [RelayCommand]
    private void BeginRename(EmulatorItemViewModel item)
    {
        foreach (var other in Emulators) other.IsRenaming = false;
        item.NewName = item.DisplayName;
        item.RenameError = null;
        item.IsRenaming = true;
    }

    [RelayCommand]
    private void CancelRename(EmulatorItemViewModel item) => item.IsRenaming = false;

    /// <summary>
    /// Only the name it's shown by changes, in Android Studio too. Its AVD name stays, so whatever starts it by that name
    /// (flutter emulators --launch, say) still can.
    /// </summary>
    [RelayCommand]
    private void SaveRename(EmulatorItemViewModel item)
    {
        var name = item.NewName.Trim();
        if (name == item.DisplayName)
        {
            item.IsRenaming = false;
            return;
        }
        if (name.Length == 0)
        {
            item.RenameError = "Give it a name.";
            return;
        }
        if (IsNameTaken(name, except: item))
        {
            item.RenameError = "Another emulator is called that. Give this one a name of its own, so the device lists can tell them apart.";
            return;
        }

        try
        {
            _emulators.Rename(item.Name, name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            item.RenameError = $"Couldn't rename it: {e.Message}";
            return;
        }

        var old = item.DisplayName;
        item.DisplayName = name;
        item.IsRenaming = false;
        Status = $"Renamed {old} to {name}.";
        OnPropertyChanged(nameof(NameError));
        Changed?.Invoke();
    }

    [RelayCommand]
    private void Delete(EmulatorItemViewModel item) => _ = DeleteAsync(item);

    private async Task DeleteAsync(EmulatorItemViewModel item)
    {
        if (!item.CanDelete) return;
        if (MessageBox.Show(
                $"Delete the emulator {item.DisplayName}?\n\nIts apps, files and settings go with it. This can't be undone.",
                "Delete emulator", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        item.IsDeleting = true;
        Status = $"Deleting {item.DisplayName}…";
        try
        {
            // It may have been started some other way since the list was read.
            if ((await _emulators.GetRunningAsync()).ContainsKey(item.Name))
            {
                item.IsRunning = true;
                Status = $"{item.DisplayName} is running. Shut it down, then delete it.";
                return;
            }
            await Task.Run(() => _emulators.Delete(item.Name));
            Emulators.Remove(item);
            Status = $"Deleted {item.DisplayName}.";
            Changed?.Invoke();
        }
        catch (Exception e)
        {
            Status = $"Couldn't delete {item.DisplayName}: {e.Message}";
        }
        finally
        {
            item.IsDeleting = false;
        }
        await RefreshAsync();
    }
}
