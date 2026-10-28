using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Application = System.Windows.Application;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoNLauncher.Models;
using RepoNLauncher.Services;

namespace RepoNLauncher.ViewModels;

/// <summary>
/// The Android screen mirror shown on the right of the dashboard (or popped out into its own window),
/// independent of which project is selected.
/// </summary>
public partial class PhoneScreenViewModel : ObservableObject
{
    private readonly IScrcpyService _scrcpy;
    private ScrcpySession? _session;
    private AndroidDevice? _mirroredDevice;

    public ObservableCollection<AndroidDevice> Devices { get; } = new();

    [ObservableProperty] private AndroidDevice? _selectedDevice;
    [ObservableProperty] private bool _isPanelVisible = true;
    [ObservableProperty] private bool _isAudioEnabled = true;
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private bool _isMirroring;
    [ObservableProperty] private bool _isFullScreen;
    [ObservableProperty] private string _status = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInstallPrompt), nameof(ShowPoppedOutNote), nameof(ShowIdleNote))]
    private bool _isScrcpyInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DockedScreenHandle), nameof(PopOutScreenHandle), nameof(HasDockedScreen))]
    [NotifyPropertyChangedFor(nameof(ShowPoppedOutNote), nameof(ShowIdleNote))]
    private bool _isPoppedOut;

    /// <summary>The scrcpy window, once it exists.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DockedScreenHandle), nameof(PopOutScreenHandle), nameof(HasDockedScreen))]
    [NotifyPropertyChangedFor(nameof(HasScreen), nameof(ShowIdleNote))]
    private IntPtr _screenHandle;

    // Only one host may own the scrcpy window at a time, so the docked and pop-out hosts each bind to their own handle.
    public IntPtr DockedScreenHandle => IsPoppedOut ? IntPtr.Zero : ScreenHandle;
    public IntPtr PopOutScreenHandle => IsPoppedOut ? ScreenHandle : IntPtr.Zero;

    public bool HasScreen => ScreenHandle != IntPtr.Zero;
    public bool HasDockedScreen => DockedScreenHandle != IntPtr.Zero;

    public bool ShowInstallPrompt => !IsScrcpyInstalled;
    public bool ShowPoppedOutNote => IsScrcpyInstalled && IsPoppedOut;
    public bool ShowIdleNote => IsScrcpyInstalled && !IsPoppedOut && !HasScreen;

    public PhoneScreenViewModel(IScrcpyService scrcpy)
    {
        _scrcpy = scrcpy;
        IsScrcpyInstalled = _scrcpy.FindScrcpy() != null;
        _ = RefreshDevicesAsync();
    }

    partial void OnSelectedDeviceChanged(AndroidDevice? value)
    {
        // Picking another device while one is showing switches the mirror over to it.
        if (value != null && _session != null && _session.Serial != value.Serial) StartMirroring(value);
    }

    partial void OnIsAudioEnabledChanged(bool value)
    {
        // Audio forwarding is fixed when scrcpy starts, so apply the change by restarting the mirror.
        if (_session != null && _mirroredDevice != null) StartMirroring(_mirroredDevice);
    }

    partial void OnIsPoppedOutChanged(bool value)
    {
        if (!value) IsFullScreen = false;
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

        if (_session != null) return;
        var unauthorized = all.FirstOrDefault(d => d.State == "unauthorized");
        Status = Devices.Count > 0
            ? "Pick a device and press Show."
            : unauthorized != null
                ? $"{unauthorized.Serial} is waiting for you to allow USB debugging on the phone."
                : "No Android device found. Connect a phone with USB debugging on, or start an emulator with the phone button above.";
    }

    [RelayCommand]
    public void Show()
    {
        if (SelectedDevice == null)
        {
            Status = "No device selected.";
            return;
        }
        StartMirroring(SelectedDevice);
    }

    [RelayCommand]
    public void Stop()
    {
        StopMirroring();
        Status = "Stopped.";
    }

    [RelayCommand]
    public void ToggleAudio() => IsAudioEnabled = !IsAudioEnabled;

    [RelayCommand]
    public void TogglePanel() => IsPanelVisible = !IsPanelVisible;

    [RelayCommand]
    public void TogglePopOut() => IsPoppedOut = !IsPoppedOut;

    /// <summary>Full screen only exists for the popped-out window, so going full screen from the panel pops it out first.</summary>
    [RelayCommand]
    public void ToggleFullScreen()
    {
        IsPoppedOut = true;
        IsFullScreen = !IsFullScreen;
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

    /// <summary>Shows the device a Flutter app was just launched on, so its screen appears without extra clicks.</summary>
    public async Task ShowRunDeviceAsync(DeviceInfo device)
    {
        // Flutter also runs on Windows/Chrome/iOS; scrcpy can only mirror Android (incl. emulators).
        if (!device.Platform.StartsWith("android", StringComparison.OrdinalIgnoreCase)) return;

        IsPanelVisible = true;
        if (_session?.Serial == device.Id) return;

        await RefreshDevicesAsync();
        var target = Devices.FirstOrDefault(d => d.Serial == device.Id); // Flutter's Android device id is the adb serial
        if (target == null)
        {
            Status = $"{device.Name} isn't listed by adb, so its screen can't be shown.";
            return;
        }

        SelectedDevice = target;
        if (_session?.Serial != target.Serial) StartMirroring(target);
    }

    public void Shutdown() => StopMirroring();

    private void StartMirroring(AndroidDevice device)
    {
        StopMirroring();

        var scrcpyPath = _scrcpy.FindScrcpy();
        IsScrcpyInstalled = scrcpyPath != null;
        if (scrcpyPath == null)
        {
            IsPanelVisible = true; // the panel's install prompt explains what's missing
            return;
        }

        ScrcpySession session;
        try
        {
            session = _scrcpy.Start(scrcpyPath, device.Serial, IsAudioEnabled);
        }
        catch (Win32Exception ex)
        {
            Status = $"Could not start scrcpy: {ex.Message}";
            return;
        }

        _session = session;
        _mirroredDevice = device;
        IsMirroring = true;
        Status = $"Connecting to {device.Model}...";
        session.Exited += () => Application.Current?.Dispatcher.BeginInvoke(() => OnSessionExited(session));
        _ = AttachWhenReadyAsync(session, device);
    }

    private async Task AttachWhenReadyAsync(ScrcpySession session, AndroidDevice device)
    {
        var hwnd = await session.WaitForWindowAsync(TimeSpan.FromSeconds(30));
        if (_session != session) return; // stopped or replaced meanwhile
        if (hwnd == IntPtr.Zero)
        {
            if (session.HasExited)
            {
                // Normally already handled via Exited, but scrcpy may have died before that was subscribed.
                OnSessionExited(session);
                return;
            }
            StopMirroring();
            Status = $"Timed out waiting for {device.Model}'s screen.";
            return;
        }

        ScreenHandle = hwnd;
        Status = $"Showing {device.Model}" + (session.LastWarning is { } warning ? $" — {warning}" : ".");
    }

    private void OnSessionExited(ScrcpySession session)
    {
        if (_session != session) return; // a deliberate stop/restart already moved on

        _session = null;
        _mirroredDevice = null;
        ScreenHandle = IntPtr.Zero;
        IsMirroring = false;
        Status = session.LastError is { } error ? $"Mirroring stopped: {error}" : "Mirroring stopped.";
    }

    private void StopMirroring()
    {
        var session = _session;
        _session = null;
        _mirroredDevice = null;

        // Detach the window from its host before killing scrcpy so it never lingers half-destroyed in the panel.
        ScreenHandle = IntPtr.Zero;
        IsMirroring = false;
        session?.Stop();
    }
}
