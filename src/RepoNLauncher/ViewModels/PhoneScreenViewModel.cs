using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoNLauncher.Models;
using RepoNLauncher.Services;

namespace RepoNLauncher.ViewModels;

/// <summary>
/// The Android screen mirrors shown on the right of the dashboard, independent of which project is selected. Several
/// devices can show at once: side by side in the panel, or each popped out into its own window. The panel's tools work on
/// the device picked in its list; each device's own are on its <see cref="DeviceScreenViewModel"/>. The list also has
/// the emulators on this PC that aren't running: Show starts one, then shows its screen.
/// </summary>
public partial class PhoneScreenViewModel : ObservableObject
{
    private readonly IScrcpyService _scrcpy;
    private readonly IDeviceToolsService _tools;
    private readonly IEmulatorService _emulators;
    private readonly EmulatorsViewModel _emulatorList;
    // Emulators started by Show, by AVD name, whose screen shows here once they're up.
    private readonly HashSet<string> _startingHere = new(StringComparer.OrdinalIgnoreCase);

    // Every device picked or shown so far, by serial, so one unplugged and plugged back in keeps what its tools had.
    private readonly Dictionary<string, DeviceScreenViewModel> _screens = new();

    public ObservableCollection<AndroidDevice> Devices { get; } = new();

    /// <summary>The devices showing, in the order they were shown, whether in the panel or popped out.</summary>
    public ObservableCollection<DeviceScreenViewModel> Screens { get; } = new();

    /// <summary>Raised when a device is popped out or docked back, for its window to open or close.</summary>
    public event Action<DeviceScreenViewModel>? PopOutChanged;

    /// <summary>
    /// Finds the project whose app runs on a device, for a device's Console button: the project, with its run on the
    /// device picked; null when the dashboard hasn't run anything there.
    /// </summary>
    public Func<AndroidDevice, ProjectViewModel?>? FindRunOn { get; set; }

    [ObservableProperty] private AndroidDevice? _selectedDevice;

    /// <summary>The device picked in the list, which the panel's tools work on.</summary>
    [ObservableProperty] private DeviceScreenViewModel? _selectedScreen;

    [ObservableProperty] private bool _isPanelVisible = true;
    [ObservableProperty] private bool _isAudioEnabled = true;
    [ObservableProperty] private bool _isInstalling;

    /// <summary>The latest news from the panel or any device.</summary>
    [ObservableProperty] private string _status = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInstallPrompt), nameof(ShowPoppedOutNote), nameof(ShowIdleNote))]
    private bool _isScrcpyInstalled;

    /// <summary>How many devices show in the panel itself, side by side.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDockEmpty), nameof(HasSeveralDockedScreens), nameof(ShowPoppedOutNote), nameof(ShowIdleNote))]
    private int _dockedScreenCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PoppedOutNote), nameof(ShowPoppedOutNote), nameof(ShowIdleNote))]
    private int _poppedOutCount;

    public bool IsDockEmpty => DockedScreenCount == 0;

    /// <summary>Each device in the panel then says which it is, and the one picked is marked.</summary>
    public bool HasSeveralDockedScreens => DockedScreenCount > 1;

    public bool ShowInstallPrompt => !IsScrcpyInstalled;
    public bool ShowPoppedOutNote => IsScrcpyInstalled && IsDockEmpty && PoppedOutCount > 0;
    public bool ShowIdleNote => IsScrcpyInstalled && IsDockEmpty && PoppedOutCount == 0;

    public string PoppedOutNote => PoppedOutCount == 1 ? "Showing in a separate window" : $"Showing in {PoppedOutCount} separate windows";

    public PhoneScreenViewModel(IScrcpyService scrcpy, IDeviceToolsService tools, IEmulatorService emulators, EmulatorsViewModel emulatorList)
    {
        _scrcpy = scrcpy;
        _tools = tools;
        _emulators = emulators;
        _emulatorList = emulatorList;
        IsScrcpyInstalled = _scrcpy.FindScrcpy() != null;
        _ = RefreshDevicesAsync();
    }

    // Picking another device leaves any showing as they are: the tools just work on the one picked.
    // An emulator that isn't running has no screen or tools yet.
    partial void OnSelectedDeviceChanged(AndroidDevice? value) =>
        SelectedScreen = value is null or { IsEmulatorToStart: true } ? null : ScreenFor(value);

    partial void OnSelectedScreenChanged(DeviceScreenViewModel? value)
    {
        foreach (var screen in _screens.Values) screen.IsSelected = screen == value;
    }

    partial void OnIsAudioEnabledChanged(bool value)
    {
        foreach (var screen in Screens.ToList()) screen.ApplyAudioSetting();
    }

    [RelayCommand]
    public async Task RefreshDevicesAsync()
    {
        var all = await _scrcpy.GetDevicesAsync();
        var ready = all.Where(d => d.IsReady).ToList();

        // The emulators running, by serial, shown by their own names: Android calls them all sdk gphone64 x86 64. The
        // rest of this PC's emulators are listed after the devices, for Show to start one.
        var avds = await _emulators.GetAvdNamesAsync(ready.Select(d => d.Serial));
        var onThisPc = _emulators.HasEmulator ? _emulators.GetEmulators() : new List<AndroidEmulator>();
        string NameOf(string avd) => onThisPc.FirstOrDefault(e => SameAvd(e.Name, avd))?.DisplayName ?? avd.Replace('_', ' ');
        var toStart = onThisPc.Where(e => !avds.Values.Any(avd => SameAvd(avd, e.Name))).ToList();
        foreach (var device in ready)
        {
            if (avds.TryGetValue(device.Serial, out var avd)) device.Model = NameOf(avd);
        }

        // Update in place so the current selection — and with it a running mirror — isn't disturbed. A name found again
        // is renamed, for when another emulator has started under one that closed's serial, or one was renamed.
        var pickedToStart = SelectedDevice?.EmulatorToStart;
        foreach (var gone in Devices.Where(d => d.EmulatorToStart is { } avd
                     ? !toStart.Any(e => SameAvd(e.Name, avd))
                     : ready.All(r => r.Serial != d.Serial)).ToList())
            Devices.Remove(gone);
        foreach (var listed in Devices)
        {
            if (listed.EmulatorToStart is { } avd)
            {
                listed.Model = NameOf(avd);
                listed.IsStarting = _emulatorList.IsStarting(avd);
            }
            else if (avds.TryGetValue(listed.Serial, out var running))
            {
                listed.Model = NameOf(running);
            }
        }
        var connected = Devices.Count(d => !d.IsEmulatorToStart);
        foreach (var added in ready.Where(r => Devices.All(d => d.Serial != r.Serial))) Devices.Insert(connected++, added);
        foreach (var emulator in toStart.Where(e => !Devices.Any(d => d.EmulatorToStart is { } avd && SameAvd(avd, e.Name))))
        {
            Devices.Add(new AndroidDevice
            {
                Model = emulator.DisplayName,
                EmulatorToStart = emulator.Name,
                IsStarting = _emulatorList.IsStarting(emulator.Name),
            });
        }

        // An emulator picked to start that's running now is picked as the device it has become.
        if (SelectedDevice != null && !Devices.Contains(SelectedDevice)) SelectedDevice = null;
        if (SelectedDevice == null && pickedToStart != null && avds.FirstOrDefault(p => SameAvd(p.Value, pickedToStart)).Key is { } serial)
            SelectedDevice = Devices.FirstOrDefault(d => d.Serial == serial);
        SelectedDevice ??= Devices.FirstOrDefault();

        if (_screens.Values.Any(s => s.IsInUse) || _startingHere.Count > 0) return;
        var unauthorized = all.FirstOrDefault(d => d.State == "unauthorized");
        Status = connected > 0
            ? "Pick a device and press Show."
            : unauthorized != null
                ? $"{unauthorized.Serial} is waiting for you to allow USB debugging on the phone."
                : Devices.Count > 0
                    ? "Pick an emulator and press Show to start it, or connect a phone with USB debugging on."
                    : "No Android device found. Connect a phone with USB debugging on, or make an emulator with the phone button above.";
    }

    private static bool SameAvd(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Starts an emulator picked in the list, in its own window as the Emulators window does, then shows its screen here
    /// once Android is up. A start already going, from the Emulators window say, is waited for rather than begun again.
    /// </summary>
    private async Task StartEmulatorAsync(AndroidDevice emulator)
    {
        var avd = emulator.EmulatorToStart!;
        if (!_startingHere.Add(avd)) return;

        var name = emulator.Model;
        emulator.IsStarting = true;
        Status = $"Starting {name}…";
        try
        {
            await _emulatorList.StartAsync(avd, new Progress<string>(step => Status = $"{name}: {step} Its screen shows here once it's up."));
        }
        catch (Exception e)
        {
            Status = $"{name} didn't start: {e.Message}";
            return;
        }
        finally
        {
            _startingHere.Remove(avd);
            emulator.IsStarting = false;
        }

        // Running, it's a device adb lists, under a serial of its own.
        var serial = (await _emulators.GetRunningAsync()).GetValueOrDefault(avd);
        await RefreshDevicesAsync();
        if (serial == null || Devices.FirstOrDefault(d => d.Serial == serial) is not { } device)
        {
            Status = $"{name} started, but adb doesn't list it, so its screen can't be shown.";
            return;
        }
        IsPanelVisible = true;
        SelectedDevice = device;
        await ScreenFor(device).ShowScreenAsync();
    }

    /// <summary>Shows the device picked in the list, beside any already showing.</summary>
    [RelayCommand]
    public void Show()
    {
        if (SelectedDevice is { IsEmulatorToStart: true } emulator)
        {
            _ = StartEmulatorAsync(emulator);
            return;
        }
        if (SelectedScreen is not { } screen)
        {
            Status = "No device selected.";
            return;
        }
        screen.Show();
    }

    [RelayCommand]
    public void Stop() => SelectedScreen?.Stop();

    [RelayCommand]
    public void ToggleAudio() => IsAudioEnabled = !IsAudioEnabled;

    [RelayCommand]
    public void TogglePanel() => IsPanelVisible = !IsPanelVisible;

    [RelayCommand]
    public void TogglePopOut()
    {
        if (SelectedScreen is not { } screen)
        {
            Status = "No device selected.";
            return;
        }
        screen.TogglePopOut();
    }

    [RelayCommand]
    public void ToggleFullScreen()
    {
        if (SelectedScreen is not { } screen)
        {
            Status = "No device selected.";
            return;
        }
        screen.ToggleFullScreen();
    }

    /// <summary>Puts every popped-out device back in the panel, closing their windows.</summary>
    [RelayCommand]
    public void DockAll()
    {
        foreach (var screen in _screens.Values) screen.IsPoppedOut = false;
    }

    /// <summary>Picks a device showing in the panel, for the panel's tools to work on.</summary>
    public void Select(DeviceScreenViewModel screen)
    {
        if (Devices.FirstOrDefault(d => d.Serial == screen.Device.Serial) is { } device) SelectedDevice = device;
    }

    [RelayCommand]
    public async Task InstallScrcpyAsync()
    {
        if (IsInstalling) return;

        IsInstalling = true;
        try
        {
            await _scrcpy.InstallAsync(new Progress<string>(message => Status = message));
            IsScrcpyInstalled = true;
            Status = "scrcpy installed.";
            await RefreshDevicesAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException
                                       or InvalidOperationException or JsonException or TaskCanceledException)
        {
            Status = $"Install failed: {ex.Message}";
        }
        finally
        {
            IsInstalling = false;
        }
    }

    /// <summary>
    /// Shows the device a Flutter app was just launched on, beside any others showing, so its screen appears without
    /// extra clicks; it's picked for the tools too.
    /// </summary>
    public async Task ShowRunDeviceAsync(DeviceInfo device)
    {
        // Flutter also runs on Windows/Chrome/iOS; scrcpy can only mirror Android (incl. emulators).
        if (!device.Platform.StartsWith("android", StringComparison.OrdinalIgnoreCase)) return;

        IsPanelVisible = true;
        if (_screens.TryGetValue(device.Id, out var shown) && shown.IsShowingScreen)
        {
            Select(shown);
            return;
        }

        await RefreshDevicesAsync();
        var target = Devices.FirstOrDefault(d => d.Serial == device.Id); // Flutter's Android device id is the adb serial
        if (target == null)
        {
            Status = $"{device.Name} isn't listed by adb, so its screen can't be shown.";
            return;
        }

        SelectedDevice = target;
        // The app is what's wanted in view, not a camera.
        await ScreenFor(target).ShowScreenAsync();
    }

    /// <summary>
    /// Stops every mirror and finishes any recording so it plays, then gives phones their real location back. Blocks for
    /// a few seconds at most; nothing it waits on needs this (UI) thread.
    /// </summary>
    public void Shutdown()
    {
        var finishing = _screens.Values.SelectMany(screen => screen.Shutdown()).ToArray();
        Task.WaitAll(finishing, DeviceScreenViewModel.FinishTimeout + TimeSpan.FromSeconds(1));
        foreach (var screen in _screens.Values) screen.Location.Shutdown();
    }

    /// <summary>scrcpy.exe, or null when it isn't installed: the panel then shows, with its prompt to install it.</summary>
    internal string? FindScrcpy()
    {
        var path = _scrcpy.FindScrcpy();
        IsScrcpyInstalled = path != null;
        if (path == null) IsPanelVisible = true;
        return path;
    }

    internal bool IsConnected(AndroidDevice device) => Devices.Any(d => d.Serial == device.Serial);

    internal void OnScreenShownChanged(DeviceScreenViewModel screen)
    {
        if (!screen.IsMirroring) Screens.Remove(screen);
        else if (!Screens.Contains(screen)) Screens.Add(screen);
        CountScreens();
    }

    internal void OnScreenPoppedOutChanged(DeviceScreenViewModel screen)
    {
        CountScreens();
        PopOutChanged?.Invoke(screen);
    }

    private void CountScreens()
    {
        DockedScreenCount = Screens.Count(s => s.IsDocked);
        PoppedOutCount = _screens.Values.Count(s => s.IsPoppedOut);
    }

    private DeviceScreenViewModel ScreenFor(AndroidDevice device)
    {
        if (!_screens.TryGetValue(device.Serial, out var screen))
        {
            screen = new DeviceScreenViewModel(this, device, _scrcpy, _tools);
            _screens[device.Serial] = screen;
        }
        return screen;
    }
}
