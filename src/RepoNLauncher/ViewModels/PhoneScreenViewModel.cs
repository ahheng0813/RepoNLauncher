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
/// the device picked in its list; each device's own are on its <see cref="DeviceScreenViewModel"/>.
/// </summary>
public partial class PhoneScreenViewModel : ObservableObject
{
    private readonly IScrcpyService _scrcpy;
    private readonly IDeviceToolsService _tools;

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

    public PhoneScreenViewModel(IScrcpyService scrcpy, IDeviceToolsService tools)
    {
        _scrcpy = scrcpy;
        _tools = tools;
        IsScrcpyInstalled = _scrcpy.FindScrcpy() != null;
        _ = RefreshDevicesAsync();
    }

    // Picking another device leaves any showing as they are: the tools just work on the one picked.
    partial void OnSelectedDeviceChanged(AndroidDevice? value) => SelectedScreen = value == null ? null : ScreenFor(value);

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

        // Update in place so the current selection — and with it a running mirror — isn't disturbed.
        foreach (var gone in Devices.Where(d => ready.All(r => r.Serial != d.Serial)).ToList()) Devices.Remove(gone);
        foreach (var added in ready.Where(r => Devices.All(d => d.Serial != r.Serial))) Devices.Add(added);
        SelectedDevice ??= Devices.FirstOrDefault();

        if (_screens.Values.Any(s => s.IsInUse)) return;
        var unauthorized = all.FirstOrDefault(d => d.State == "unauthorized");
        Status = Devices.Count > 0
            ? "Pick a device and press Show."
            : unauthorized != null
                ? $"{unauthorized.Serial} is waiting for you to allow USB debugging on the phone."
                : "No Android device found. Connect a phone with USB debugging on, or start an emulator with the phone button above.";
    }

    /// <summary>Shows the device picked in the list, beside any already showing.</summary>
    [RelayCommand]
    public void Show()
    {
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
