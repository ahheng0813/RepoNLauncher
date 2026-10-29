using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoNLauncher.Models;
using RepoNLauncher.Services;

namespace RepoNLauncher.ViewModels;

/// <summary>
/// The Android screen mirror shown on the right of the dashboard (or popped out into its own window),
/// independent of which project is selected. Its tools work on the selected device: screenshots, recording the screen
/// or a camera, recording the microphone, sending files, and a virtual location.
/// </summary>
public partial class PhoneScreenViewModel : ObservableObject
{
    // How long scrcpy gets to finish writing a recording before it's cut off.
    private static readonly TimeSpan FinishTimeout = TimeSpan.FromSeconds(10);

    private readonly IScrcpyService _scrcpy;
    private readonly IDeviceToolsService _tools;
    private readonly DispatcherTimer _recordingClock = new() { Interval = TimeSpan.FromSeconds(1) };
    private ScrcpySession? _session;
    private AndroidDevice? _mirroredDevice;
    private ScrcpySession? _voice;
    private DateTime _screenRecordingStarted;
    private DateTime _voiceRecordingStarted;

    // Bumped by every start and stop of the mirror, so a start still waiting for a recording to be saved knows it's been overtaken.
    private int _mirrorRequest;
    private Task _savingRecording = Task.CompletedTask;
    // Recordings scrcpy is still writing out; closing the dashboard waits for them.
    private readonly List<Task> _finishing = new();

    public ObservableCollection<AndroidDevice> Devices { get; } = new();

    public DeviceLocationViewModel Location { get; }

    [ObservableProperty] private AndroidDevice? _selectedDevice;
    [ObservableProperty] private bool _isPanelVisible = true;
    [ObservableProperty] private bool _isAudioEnabled = true;
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private bool _isMirroring;
    [ObservableProperty] private bool _isFullScreen;
    [ObservableProperty] private string _status = string.Empty;

    /// <summary>What the mirror shows, or will once it starts: the screen or a camera.</summary>
    [ObservableProperty] private MirrorSource _source = MirrorSource.Screen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecording))]
    private bool _isRecordingScreen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecording))]
    private bool _isRecordingVoice;

    public bool IsRecording => IsRecordingScreen || IsRecordingVoice;

    /// <summary>"Screen" or "Camera": what the recording in progress is of.</summary>
    [ObservableProperty] private string _screenRecordingLabel = "Screen";
    [ObservableProperty] private string _screenRecordingTime = "0:00";
    [ObservableProperty] private string _voiceRecordingTime = "0:00";

    [ObservableProperty] private bool _isSendingFiles;

    /// <summary>The screenshot or recording saved last, offered to open until another is saved.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastCaptureName))]
    private string? _lastCapturePath;

    public string? LastCaptureName => Path.GetFileName(LastCapturePath);

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

    public PhoneScreenViewModel(IScrcpyService scrcpy, IDeviceToolsService tools)
    {
        _scrcpy = scrcpy;
        _tools = tools;
        Location = new DeviceLocationViewModel(tools, () => SelectedDevice);
        _recordingClock.Tick += (_, _) => UpdateRecordingTimes();
        IsScrcpyInstalled = _scrcpy.FindScrcpy() != null;
        _ = RefreshDevicesAsync();
    }

    partial void OnSelectedDeviceChanged(AndroidDevice? value)
    {
        // Picking another device while one is showing switches the mirror over to it.
        if (value != null && _session != null && _session.Serial != value.Serial) _ = StartMirroringAsync(value);
    }

    partial void OnIsAudioEnabledChanged(bool value)
    {
        // Audio forwarding is fixed when scrcpy starts, so apply the change by restarting the mirror. A camera has no sound live.
        if (_session is { Source: MirrorSource.Screen } && _mirroredDevice != null) _ = StartMirroringAsync(_mirroredDevice);
    }

    partial void OnIsPoppedOutChanged(bool value)
    {
        if (!value) IsFullScreen = false;
    }

    partial void OnIsRecordingScreenChanged(bool value) => UpdateRecordingTimes();
    partial void OnIsRecordingVoiceChanged(bool value) => UpdateRecordingTimes();

    [RelayCommand]
    public async Task RefreshDevicesAsync()
    {
        var all = await _scrcpy.GetDevicesAsync();
        var ready = all.Where(d => d.IsReady).ToList();

        // Update in place so the current selection — and with it a running mirror — isn't disturbed.
        foreach (var gone in Devices.Where(d => ready.All(r => r.Serial != d.Serial)).ToList()) Devices.Remove(gone);
        foreach (var added in ready.Where(r => Devices.All(d => d.Serial != r.Serial))) Devices.Add(added);
        SelectedDevice ??= Devices.FirstOrDefault();

        if (_session != null || IsRecording) return;
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
        _ = StartMirroringAsync(SelectedDevice);
    }

    [RelayCommand]
    public void Stop()
    {
        _mirrorRequest++;
        var saving = StopMirroringAsync();
        if (saving.IsCompleted) Status = "Stopped.";
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

    /// <summary>Shows the selected device's screen, or one of its cameras, starting the mirror if it isn't showing.</summary>
    [RelayCommand]
    private async Task ShowSourceAsync(MirrorSource source)
    {
        if (SelectedDevice is not { } device)
        {
            Status = "No device selected.";
            return;
        }

        if (source != MirrorSource.Screen && await NeedsAndroidAsync(device, 31, "Showing a camera")) return;

        Source = source;
        if (_session != null && _session.Serial == device.Serial && _session.Source == source) return;
        await StartMirroringAsync(device);
    }

    /// <summary>Saves a screenshot (or, while a camera shows, a photo) and copies it, ready to paste.</summary>
    [RelayCommand]
    private async Task TakeScreenshotAsync()
    {
        if (SelectedDevice is not { } device)
        {
            Status = "No device selected.";
            return;
        }
        try
        {
            string path;
            if (_session is { Source: not MirrorSource.Screen } camera && camera.Serial == device.Serial && HasScreen)
            {
                // The phone can't be asked for a camera picture, so it's taken from the mirror, at the mirror's size.
                path = CaptureFiles.NewPath(CaptureKind.CameraPhoto, device.Model);
                WindowCapture.SavePng(ScreenHandle, camera.VideoSize, path);
            }
            else
            {
                path = CaptureFiles.NewPath(CaptureKind.Screenshot, device.Model);
                await _tools.TakeScreenshotAsync(device.Serial, path);
            }

            LastCapturePath = path;
            Status = CopyImage(path)
                ? $"Saved {Path.GetFileName(path)}, and copied it to paste anywhere."
                : $"Saved {Path.GetFileName(path)}.";
        }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException or IOException or UnauthorizedAccessException or ExternalException)
        {
            Status = $"Couldn't take a screenshot of {device.Model}: {e.Message}";
        }
    }

    /// <summary>
    /// Records what the mirror shows, starting it if needed, with the phone's sound while it plays on this PC (a camera
    /// records the microphone). Pressed again, it saves the recording and carries on mirroring.
    /// </summary>
    [RelayCommand]
    private async Task ToggleScreenRecordingAsync()
    {
        if (IsRecordingScreen)
        {
            // scrcpy can't stop recording and keep going, so the mirror starts again without it.
            if (_mirroredDevice is { } mirrored) await StartMirroringAsync(mirrored);
            return;
        }

        if (SelectedDevice is not { } device)
        {
            Status = "No device selected.";
            return;
        }
        var kind = Source == MirrorSource.Screen ? CaptureKind.ScreenRecording : CaptureKind.CameraRecording;
        await StartMirroringAsync(device, CaptureFiles.NewPath(kind, device.Model));
    }

    /// <summary>Records the selected device's microphone to an .m4a file, alongside the mirror; pressed again, saves it.</summary>
    [RelayCommand]
    private async Task ToggleVoiceRecordingAsync()
    {
        if (_voice is { } recording)
        {
            _voice = null;
            IsRecordingVoice = false;
            await SaveRecordingAsync(recording, "voice recording");
            return;
        }

        if (SelectedDevice is not { } device)
        {
            Status = "No device selected.";
            return;
        }
        var scrcpyPath = _scrcpy.FindScrcpy();
        IsScrcpyInstalled = scrcpyPath != null;
        if (scrcpyPath == null)
        {
            IsPanelVisible = true; // the panel's install prompt explains what's missing
            Status = "Recording needs scrcpy.";
            return;
        }
        if (await NeedsAndroidAsync(device, 30, "Recording from the microphone")) return;

        ScrcpySession session;
        try
        {
            session = _scrcpy.StartVoiceRecording(scrcpyPath, device.Serial, CaptureFiles.NewPath(CaptureKind.VoiceRecording, device.Model));
        }
        catch (Win32Exception ex)
        {
            Status = $"Could not start scrcpy: {ex.Message}";
            return;
        }

        _voice = session;
        _voiceRecordingStarted = DateTime.Now;
        IsRecordingVoice = true;
        Status = $"Recording {device.Model}'s microphone…";
        session.Exited += () => Application.Current?.Dispatcher.BeginInvoke(() => OnVoiceExited(session));
    }

    [RelayCommand]
    private async Task ChooseFilesToSendAsync()
    {
        if (SelectedDevice is not { } device)
        {
            Status = "No device selected.";
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog { Title = $"Send files to {device.Model}", Multiselect = true };
        if (dialog.ShowDialog() != true) return;
        await SendFilesAsync(dialog.FileNames);
    }

    /// <summary>Copies files or folders (chosen, or dropped on the panel) to the selected device's Download folder.</summary>
    public async Task SendFilesAsync(IReadOnlyList<string> paths)
    {
        if (SelectedDevice is not { } device)
        {
            Status = "No device selected.";
            return;
        }
        if (paths.Count == 0 || IsSendingFiles) return;

        IsSendingFiles = true;
        var what = paths.Count == 1 ? Path.GetFileName(paths[0].TrimEnd('\\')) : $"{paths.Count} items";
        Status = $"Sending {what} to {device.Model}…";
        try
        {
            var sent = await _tools.SendFilesAsync(device.Serial, paths);
            Status = $"Sent {what} to {device.Model}'s Download folder" + (sent == null ? "." : $": {Describe(sent)}.");
        }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException)
        {
            Status = $"Couldn't send {what} to {device.Model}: {e.Message}";
        }
        finally
        {
            IsSendingFiles = false;
        }
    }

    [RelayCommand]
    private void OpenLastCapture()
    {
        if (LastCapturePath is not { } path || !File.Exists(path))
        {
            Status = "That file isn't there any more.";
            return;
        }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    [RelayCommand]
    private void ShowLastCaptureInFolder()
    {
        if (LastCapturePath is not { } path) return;
        Process.Start("explorer.exe", File.Exists(path) ? $"/select,\"{path}\"" : $"\"{Path.GetDirectoryName(path)}\"");
    }

    [RelayCommand]
    private void DismissLastCapture() => LastCapturePath = null;

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
        if (_session?.Serial == device.Id && _session.Source == MirrorSource.Screen) return;

        await RefreshDevicesAsync();
        var target = Devices.FirstOrDefault(d => d.Serial == device.Id); // Flutter's Android device id is the adb serial
        if (target == null)
        {
            Status = $"{device.Name} isn't listed by adb, so its screen can't be shown.";
            return;
        }

        // The app is what's wanted in view, not a camera.
        Source = MirrorSource.Screen;
        SelectedDevice = target;
        if (_session?.Serial != target.Serial || _session.Source != MirrorSource.Screen) await StartMirroringAsync(target);
    }

    /// <summary>
    /// Stops the mirror and finishes any recording so it plays, then gives a phone its real location back. Blocks for a
    /// few seconds at most; nothing it waits on needs this (UI) thread.
    /// </summary>
    public void Shutdown()
    {
        _mirrorRequest++;
        _recordingClock.Stop();

        var mirror = _session;
        _session = null;
        ScreenHandle = IntPtr.Zero;
        if (mirror?.RecordPath != null) _finishing.Add(mirror.FinishAsync(FinishTimeout));
        else mirror?.Stop();

        if (_voice is { } voice) _finishing.Add(voice.FinishAsync(FinishTimeout));
        _voice = null;

        Task.WaitAll(_finishing.ToArray(), FinishTimeout + TimeSpan.FromSeconds(1));
        Location.Shutdown();
    }

    private async Task StartMirroringAsync(AndroidDevice device, string? recordPath = null)
    {
        var request = ++_mirrorRequest;
        // Stopping a recording starts the mirror again; what saving it said stays in view with the mirror's news.
        var saved = await StopMirroringAsync();
        // A recording still being written holds on to the phone's sound; a new mirror started meanwhile would get none.
        await _savingRecording;
        if (request != _mirrorRequest) return; // stopped or started again meanwhile

        var scrcpyPath = _scrcpy.FindScrcpy();
        IsScrcpyInstalled = scrcpyPath != null;
        if (scrcpyPath == null)
        {
            IsPanelVisible = true; // the panel's install prompt explains what's missing
            return;
        }

        // The screen's sound plays here when it's on, and goes in a recording with it. A camera's recording has the microphone's.
        var sound = Source == MirrorSource.Screen ? IsAudioEnabled : recordPath != null;
        ScrcpySession session;
        try
        {
            session = _scrcpy.Start(scrcpyPath, device.Serial, sound, Source, recordPath);
        }
        catch (Win32Exception ex)
        {
            Status = $"Could not start scrcpy: {ex.Message}";
            return;
        }

        _session = session;
        _mirroredDevice = device;
        IsMirroring = true;
        if (recordPath != null)
        {
            _screenRecordingStarted = DateTime.Now;
            ScreenRecordingLabel = session.Source == MirrorSource.Screen ? "Screen" : "Camera";
            IsRecordingScreen = true;
        }
        Status = Join(saved, $"Connecting to {device.Model}...");
        session.Exited += () => Application.Current?.Dispatcher.BeginInvoke(() => OnSessionExited(session));
        _ = AttachWhenReadyAsync(session, device, saved);
    }

    private async Task AttachWhenReadyAsync(ScrcpySession session, AndroidDevice device, string? note)
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
            _mirrorRequest++;
            _ = StopMirroringAsync();
            Status = $"Timed out waiting for {device.Model}'s screen.";
            return;
        }

        ScreenHandle = hwnd;
        var what = session.Source switch
        {
            MirrorSource.BackCamera => $"{device.Model}'s back camera",
            MirrorSource.FrontCamera => $"{device.Model}'s front camera",
            _ => device.Model,
        };
        Status = Join(note, (session.RecordPath != null ? $"Recording {what}" : $"Showing {what}")
                            + (session.LastWarning is { } warning ? $" — {warning}" : "."));
    }

    private static string Join(string? first, string then) => first == null ? then : $"{first} {then}";

    private void OnSessionExited(ScrcpySession session)
    {
        if (_session != session) return; // a deliberate stop/restart already moved on

        _session = null;
        _mirroredDevice = null;
        ScreenHandle = IntPtr.Zero;
        IsMirroring = false;
        // A camera that won't start would only fail again; the next Show shows the screen.
        if (session.Source != MirrorSource.Screen) Source = MirrorSource.Screen;

        var why = session.LastError is { } error ? $"Mirroring stopped: {error}" : "Mirroring stopped.";
        if (session.RecordPath == null)
        {
            Status = why;
            return;
        }

        // Unplugged, say: scrcpy still finishes the file.
        IsRecordingScreen = false;
        ReportRecording(session, finished: true, why);
    }

    private void OnVoiceExited(ScrcpySession session)
    {
        if (_voice != session) return; // stopped on purpose, which reports it

        _voice = null;
        IsRecordingVoice = false;
        ReportRecording(session, finished: true,
            session.LastError is { } error ? $"Voice recording stopped: {error}." : "Voice recording stopped.");
    }

    // Detaches the mirror and ends it. A recording in it is finished and saved, which takes a moment: the task is that,
    // with what saving it said (null when there was nothing to save).
    private Task<string?> StopMirroringAsync()
    {
        var session = _session;
        _session = null;
        _mirroredDevice = null;

        // Detach the window from its host before killing scrcpy so it never lingers half-destroyed in the panel.
        ScreenHandle = IntPtr.Zero;
        IsMirroring = false;
        if (session == null) return Task.FromResult<string?>(null);
        if (session.RecordPath == null)
        {
            session.Stop();
            return Task.FromResult<string?>(null);
        }

        IsRecordingScreen = false;
        var saving = SaveRecordingAsync(session, "recording");
        _savingRecording = saving;
        return saving;
    }

    private async Task<string?> SaveRecordingAsync(ScrcpySession session, string what)
    {
        Status = $"Saving the {what}…";
        var finishing = session.FinishAsync(FinishTimeout);
        _finishing.Add(finishing);
        var finished = await finishing;
        _finishing.Remove(finishing);
        return ReportRecording(session, finished);
    }

    /// <summary>Says what a recording that has ended left behind, and offers the file; returns what it said.</summary>
    private string ReportRecording(ScrcpySession session, bool finished, string? why = null)
    {
        var file = new FileInfo(session.RecordPath!);
        var prefix = why == null ? string.Empty : why + " ";
        if (!file.Exists || file.Length == 0)
        {
            try { file.Delete(); } catch (IOException) { /* in use a moment longer; it's empty anyway */ }
            return Status = prefix + (why == null && session.LastError is { } error ? $"Nothing was recorded: {error}" : "Nothing was recorded.");
        }

        LastCapturePath = file.FullName;
        return Status = prefix + (finished ? $"Saved {file.Name}." : $"Saved {file.Name}, but it was cut off and may not play.");
    }

    // Says what's missing rather than letting scrcpy fail with a less helpful message.
    private async Task<bool> NeedsAndroidAsync(AndroidDevice device, int apiLevel, string what)
    {
        var api = await _tools.GetApiLevelAsync(device.Serial);
        if (api == 0 || api >= apiLevel) return false;

        Status = $"{what} needs {AndroidVersions.Name(apiLevel.ToString())} or later, and {device.Model} has "
                 + (AndroidVersions.Name(api.ToString()) ?? $"API {api}") + ".";
        return true;
    }

    private void UpdateRecordingTimes()
    {
        if (IsRecording) _recordingClock.Start();
        else _recordingClock.Stop();

        var now = DateTime.Now;
        if (IsRecordingScreen) ScreenRecordingTime = Elapsed(now - _screenRecordingStarted);
        if (IsRecordingVoice) VoiceRecordingTime = Elapsed(now - _voiceRecordingStarted);
    }

    private static string Elapsed(TimeSpan time) => time.ToString(time.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");

    private static string Describe(SentFiles sent)
    {
        var size = sent.Bytes switch
        {
            < 1024 * 1024 => $"{Math.Max(1, sent.Bytes / 1024)} KB",
            < 1024L * 1024 * 1024 => $"{sent.Bytes / (1024.0 * 1024):0.#} MB",
            _ => $"{sent.Bytes / (1024.0 * 1024 * 1024):0.##} GB",
        };
        return $"{sent.Files} {(sent.Files == 1 ? "file" : "files")}, {size} in {Math.Max(0.1, sent.Seconds):0.#} s";
    }

    // So a screenshot can be pasted straight into a chat or an issue.
    private static bool CopyImage(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Clipboard.SetImage(BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad));
            return true;
        }
        catch (Exception e) when (e is ExternalException or IOException or NotSupportedException)
        {
            return false; // the clipboard is busy with another app; the file is saved all the same
        }
    }
}
