using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoNLauncher.Models;
using RepoNLauncher.Services;

namespace RepoNLauncher.ViewModels;

/// <summary>An emulator in the Android emulators window.</summary>
public partial class EmulatorItemViewModel(AndroidEmulator emulator) : ObservableObject
{
    public AndroidEmulator Emulator { get; } = emulator;
    public string Name => Emulator.Name;
    public string DisplayName => Emulator.DisplayName;
    public string Details => Emulator.Details;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    private bool _isStarting;

    public bool CanStart => !IsRunning && !IsStarting;
}

/// <summary>
/// The Android emulators window: the emulators on this PC, each of which can be started, and making a new one from an
/// installed system image. It lives as long as the dashboard, so an emulator that's starting finishes with it closed.
/// </summary>
public partial class EmulatorsViewModel : ObservableObject
{
    private readonly IEmulatorService _emulators;
    // By name: a refresh replaces the rows while an emulator is still starting.
    private readonly HashSet<string> _starting = new(StringComparer.OrdinalIgnoreCase);
    private bool _suggestingName;
    private bool _nameEdited;

    public EmulatorsViewModel(IEmulatorService emulators) => _emulators = emulators;

    public ObservableCollection<EmulatorItemViewModel> Emulators { get; } = new();
    public ObservableCollection<SystemImage> Images { get; } = new();
    public IReadOnlyList<EmulatorDevice> Devices => EmulatorDevice.All;

    /// <summary>Raised when an emulator has finished starting, so the device lists can pick it up.</summary>
    public event Action? EmulatorStarted;

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
            return _emulators.Exists(name) ? $"There's already an emulator called {name}." : string.Empty;
        }
    }

    /// <summary>Reads the emulators and images again, and which emulators are running.</summary>
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

        Emulators.Clear();
        foreach (var emulator in _emulators.GetEmulators())
            Emulators.Add(new EmulatorItemViewModel(emulator) { IsStarting = _starting.Contains(emulator.Name) });

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
        for (var n = 2; _emulators.Exists(_emulators.NameFor(name)); n++) name = $"{baseName} {n}";

        _suggestingName = true;
        NewName = name;
        _suggestingName = false;
    }

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
        var created = Emulators.FirstOrDefault(e => e.Name == _emulators.NameFor(displayName));
        if (StartAfterCreate && created != null) await StartAsync(created);
    }

    [RelayCommand]
    private async Task StartAsync(EmulatorItemViewModel item)
    {
        if (!item.CanStart) return;
        var name = item.Name;
        _starting.Add(name);
        item.IsStarting = true;
        var progress = new Progress<string>(step => Status = $"{item.DisplayName}: {step} The first start of a new emulator takes a few minutes.");
        try
        {
            await _emulators.StartAsync(name, progress);
            Status = $"{item.DisplayName} is running. It's in the Phone screen's device list, and Flutter's.";
            EmulatorStarted?.Invoke();
        }
        catch (Exception e)
        {
            Status = $"{item.DisplayName} didn't start: {e.Message}";
        }
        finally
        {
            _starting.Remove(name);
            foreach (var row in Emulators.Where(e => e.Name == name)) row.IsStarting = false;
        }
        await RefreshAsync();
    }
}
