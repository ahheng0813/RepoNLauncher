using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
/// One device on the Phone screen: its mirror, in the dashboard's side panel beside any others or popped out into a window
/// of its own, and the tools that work on it — screenshots, recording the screen or a camera, recording the microphone,
/// sending files, a virtual location, and pressing its power, Back, Home and recent apps buttons.
/// </summary>
public partial class DeviceScreenViewModel : ObservableObject
{
    // How long scrcpy gets to finish writing a recording before it's cut off.
    internal static readonly TimeSpan FinishTimeout = TimeSpan.FromSeconds(10);

    private readonly PhoneScreenViewModel _phone;
    private readonly IScrcpyService _scrcpy;
    private readonly IDeviceToolsService _tools;
    private readonly DispatcherTimer _recordingClock = new() { Interval = TimeSpan.FromSeconds(1) };
    private ScrcpySession? _session;
    private ScrcpySession? _voice;
    private DateTime _screenRecordingStarted;
    private DateTime _voiceRecordingStarted;

    // Bumped by every start and stop of the mirror, so a start still waiting for a recording to be saved knows it's been overtaken.
    private int _mirrorRequest;
    private Task _savingRecording = Task.CompletedTask;
    // Recordings scrcpy is still writing out; closing the dashboard waits for them.
    private readonly List<Task> _finishing = new();

    public AndroidDevice Device { get; }

    public DeviceLocationViewModel Location { get; }

    /// <summary>
    /// Shown, or about to be: from Show until Stop, or until the mirror ends by itself. Starting it again (to record, or
    /// to show a camera) keeps it shown meanwhile.
    /// </summary>
    [ObservableProperty] private bool _isMirroring;
    [ObservableProperty] private bool _isFullScreen;
    /// <summary>Keeps the popped-out window above every other window, even when it isn't the active one.</summary>
    [ObservableProperty] private bool _isPinnedOnTop;
    /// <summary>The device picked in the panel's list, which the panel's tools work on.</summary>
    [ObservableProperty] private bool _isSelected;

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
    [NotifyPropertyChangedFor(nameof(IsDocked), nameof(DockedScreenHandle), nameof(PopOutScreenHandle), nameof(HasDockedScreen))]
    private bool _isPoppedOut;

    public bool IsDocked => !IsPoppedOut;

    /// <summary>The scrcpy window, once it exists.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DockedScreenHandle), nameof(PopOutScreenHandle), nameof(HasDockedScreen), nameof(HasScreen))]
    private IntPtr _screenHandle;

    // Only one host may own the scrcpy window at a time, so the docked and pop-out hosts each bind to their own handle.
    public IntPtr DockedScreenHandle => IsPoppedOut ? IntPtr.Zero : ScreenHandle;
    public IntPtr PopOutScreenHandle => IsPoppedOut ? ScreenHandle : IntPtr.Zero;

    public bool HasScreen => ScreenHandle != IntPtr.Zero;
    public bool HasDockedScreen => DockedScreenHandle != IntPtr.Zero;

    private string _status = string.Empty;

    /// <summary>What's happening with this device. The panel's status line shows it too, with every other device's news.</summary>
    public string Status
    {
        get => _status;
        set
        {
            SetProperty(ref _status, value);
            _phone.Status = value;
        }
    }

    /// <summary>Mirroring or recording, so the device list's hints would only be in the way.</summary>
    internal bool IsInUse => IsMirroring || IsRecording;

    /// <summary>Its own screen is showing (not a camera), or starting to.</summary>
    internal bool IsShowingScreen => _session is { Source: MirrorSource.Screen };

    public DeviceScreenViewModel(PhoneScreenViewModel phone, AndroidDevice device, IScrcpyService scrcpy, IDeviceToolsService tools)
    {
        _phone = phone;
        _scrcpy = scrcpy;
        _tools = tools;
        Device = device;
        Location = new DeviceLocationViewModel(tools, device);
        _recordingClock.Tick += (_, _) => UpdateRecordingTimes();
    }

    partial void OnIsMirroringChanged(bool value) => _phone.OnScreenShownChanged(this);

    partial void OnIsPoppedOutChanged(bool value)
    {
        if (!value) IsFullScreen = false;
        _phone.OnScreenPoppedOutChanged(this);
    }

    partial void OnIsRecordingScreenChanged(bool value) => UpdateRecordingTimes();
    partial void OnIsRecordingVoiceChanged(bool value) => UpdateRecordingTimes();

    /// <summary>Shows the device, beside any others already showing.</summary>
    [RelayCommand]
    public void Show() => _ = StartMirroringAsync();

    [RelayCommand]
    public void Stop()
    {
        _mirrorRequest++;
        var saving = StopMirroringAsync();
        if (saving.IsCompleted) Status = $"Stopped showing {Device.Model}.";
    }

    [RelayCommand]
    public void TogglePopOut() => IsPoppedOut = !IsPoppedOut;

    [RelayCommand]
    public void TogglePinnedOnTop() => IsPinnedOnTop = !IsPinnedOnTop;

    /// <summary>Full screen only exists for the popped-out window, so going full screen from the panel pops it out first.</summary>
    [RelayCommand]
    public void ToggleFullScreen()
    {
        IsPoppedOut = true;
        IsFullScreen = !IsFullScreen;
    }

    /// <summary>Shows the device's screen, or one of its cameras, starting the mirror if it isn't showing.</summary>
    [RelayCommand]
    private async Task ShowSourceAsync(MirrorSource source)
    {
        if (source != MirrorSource.Screen && await NeedsAndroidAsync(31, "Showing a camera")) return;

        Source = source;
        if (_session != null && _session.Source == source) return;
        await StartMirroringAsync();
    }

    /// <summary>Shows its screen rather than a camera, starting the mirror unless the screen shows already.</summary>
    internal async Task ShowScreenAsync()
    {
        Source = MirrorSource.Screen;
        if (!IsShowingScreen) await StartMirroringAsync();
    }

    /// <summary>Audio forwarding is fixed when scrcpy starts, so the screen's mirror starts again to apply the change. A camera has no sound live.</summary>
    internal void ApplyAudioSetting()
    {
        if (IsShowingScreen) _ = StartMirroringAsync();
    }

    /// <summary>Saves a screenshot (or, while a camera shows, a photo) and copies it, ready to paste.</summary>
    [RelayCommand]
    private async Task TakeScreenshotAsync()
    {
        try
        {
            string path;
            if (_session is { Source: not MirrorSource.Screen } camera && HasScreen)
            {
                // The phone can't be asked for a camera picture, so it's taken from the mirror, at the mirror's size.
                path = CaptureFiles.NewPath(CaptureKind.CameraPhoto, Device.Model);
                WindowCapture.SavePng(ScreenHandle, camera.VideoSize, path);
            }
            else
            {
                path = CaptureFiles.NewPath(CaptureKind.Screenshot, Device.Model);
                await _tools.TakeScreenshotAsync(Device.Serial, path);
            }

            LastCapturePath = path;
            Status = CopyImage(path)
                ? $"Saved {Path.GetFileName(path)}, and copied it to paste anywhere."
                : $"Saved {Path.GetFileName(path)}.";
        }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException or IOException or UnauthorizedAccessException or ExternalException)
        {
            Status = $"Couldn't take a screenshot of {Device.Model}: {e.Message}";
        }
    }

    /// <summary>
    /// Records what the mirror shows, starting it if needed, with the phone's sound while it plays on this PC (a camera
    /// records the microphone). Pressed again, it saves the recording and carries on mirroring.
    /// </summary>
    [RelayCommand]
    private async Task ToggleScreenRecordingAsync()
    {
        // scrcpy can't stop recording and keep going, so the mirror starts again without it.
        if (IsRecordingScreen)
        {
            await StartMirroringAsync();
            return;
        }

        var kind = Source == MirrorSource.Screen ? CaptureKind.ScreenRecording : CaptureKind.CameraRecording;
        await StartMirroringAsync(CaptureFiles.NewPath(kind, Device.Model));
    }

    /// <summary>Records the device's microphone to an .m4a file, alongside the mirror; pressed again, saves it.</summary>
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

        var scrcpyPath = _phone.FindScrcpy();
        if (scrcpyPath == null)
        {
            Status = "Recording needs scrcpy.";
            return;
        }
        if (await NeedsAndroidAsync(30, "Recording from the microphone")) return;

        ScrcpySession session;
        try
        {
            session = _scrcpy.StartVoiceRecording(scrcpyPath, Device.Serial, CaptureFiles.NewPath(CaptureKind.VoiceRecording, Device.Model));
        }
        catch (Win32Exception ex)
        {
            Status = $"Could not start scrcpy: {ex.Message}";
            return;
        }

        _voice = session;
        _voiceRecordingStarted = DateTime.Now;
        IsRecordingVoice = true;
        Status = $"Recording {Device.Model}'s microphone…";
        session.Exited += () => Application.Current?.Dispatcher.BeginInvoke(() => OnVoiceExited(session));
    }

    /// <summary>
    /// Presses one of the device's own buttons: power (its screen off or on), Back, Home or recent apps. Several can be
    /// pressed in a row without waiting for the one before.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task PressKeyAsync(DeviceKey key)
    {
        try
        {
            await _tools.PressKeyAsync(Device.Serial, key);
        }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException)
        {
            var what = key switch
            {
                DeviceKey.Power => "the power button",
                DeviceKey.Recents => "Recent apps",
                _ => key.ToString(),
            };
            Status = $"Couldn't press {what} on {Device.Model}: {e.Message}";
        }
    }

    /// <summary>The device's files to look through, send to and copy from; null (saying why) when it's gone.</summary>
    public PhoneFilesViewModel? OpenFiles()
    {
        if (!_phone.IsConnected(Device))
        {
            Status = $"{Device.Model} isn't connected.";
            return null;
        }
        return new PhoneFilesViewModel(_tools, Device);
    }

    /// <summary>Copies files or folders dropped on the tools to the device's Download folder.</summary>
    public async Task SendFilesAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || IsSendingFiles) return;

        IsSendingFiles = true;
        var what = paths.Count == 1 ? Path.GetFileName(paths[0].TrimEnd('\\')) : $"{paths.Count} items";
        Status = $"Sending {what} to {Device.Model}…";
        try
        {
            var sent = await _tools.SendFilesAsync(Device.Serial, paths);
            Status = $"Sent {what} to {Device.Model}'s Download folder" + (sent == null ? "." : $": {sent.Summary}.");
        }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException)
        {
            Status = $"Couldn't send {what} to {Device.Model}: {e.Message}";
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

    /// <summary>
    /// Stops the mirror and starts finishing any recording so it plays; returns the recordings still being written out,
    /// for closing the dashboard to wait on.
    /// </summary>
    internal Task[] Shutdown()
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
        return _finishing.ToArray();
    }

    private async Task StartMirroringAsync(string? recordPath = null)
    {
        var request = ++_mirrorRequest;
        // Stopping a recording starts the mirror again; what saving it said stays in view with the mirror's news.
        var saved = await StopMirroringAsync(restarting: true);
        // A recording still being written holds on to the phone's sound; a new mirror started meanwhile would get none.
        await _savingRecording;
        if (request != _mirrorRequest) return; // stopped or started again meanwhile

        var scrcpyPath = _phone.FindScrcpy();
        if (scrcpyPath == null)
        {
            IsMirroring = false;
            return;
        }

        // The screen's sound plays here when it's on, and goes in a recording with it. A camera's recording has the microphone's.
        var sound = Source == MirrorSource.Screen ? _phone.IsAudioEnabled : recordPath != null;
        ScrcpySession session;
        try
        {
            session = _scrcpy.Start(scrcpyPath, Device.Serial, sound, Source, recordPath);
        }
        catch (Win32Exception ex)
        {
            IsMirroring = false;
            Status = $"Could not start scrcpy: {ex.Message}";
            return;
        }

        _session = session;
        IsMirroring = true;
        if (recordPath != null)
        {
            _screenRecordingStarted = DateTime.Now;
            ScreenRecordingLabel = session.Source == MirrorSource.Screen ? "Screen" : "Camera";
            IsRecordingScreen = true;
        }
        Status = Join(saved, $"Connecting to {Device.Model}...");
        session.Exited += () => Application.Current?.Dispatcher.BeginInvoke(() => OnSessionExited(session));
        _ = AttachWhenReadyAsync(session, saved);
    }

    private async Task AttachWhenReadyAsync(ScrcpySession session, string? note)
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
            Status = $"Timed out waiting for {Device.Model}'s screen.";
            return;
        }

        ScreenHandle = hwnd;
        var what = session.Source switch
        {
            MirrorSource.BackCamera => $"{Device.Model}'s back camera",
            MirrorSource.FrontCamera => $"{Device.Model}'s front camera",
            _ => Device.Model,
        };
        Status = Join(note, (session.RecordPath != null ? $"Recording {what}" : $"Showing {what}")
                            + (session.LastWarning is { } warning ? $" — {warning}" : "."));
    }

    private static string Join(string? first, string then) => first == null ? then : $"{first} {then}";

    private void OnSessionExited(ScrcpySession session)
    {
        if (_session != session) return; // a deliberate stop/restart already moved on

        _session = null;
        ScreenHandle = IntPtr.Zero;
        IsMirroring = false;
        // A camera that won't start would only fail again; the next Show shows the screen.
        if (session.Source != MirrorSource.Screen) Source = MirrorSource.Screen;

        var why = session.LastError is { } error ? $"Mirroring {Device.Model} stopped: {error}" : $"Mirroring {Device.Model} stopped.";
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
            session.LastError is { } error ? $"Recording {Device.Model}'s voice stopped: {error}." : $"Recording {Device.Model}'s voice stopped.");
    }

    // Detaches the mirror and ends it. A recording in it is finished and saved, which takes a moment: the task is that,
    // with what saving it said (null when there was nothing to save). Restarting, it stays shown meanwhile.
    private Task<string?> StopMirroringAsync(bool restarting = false)
    {
        var session = _session;
        _session = null;

        // Detach the window from its host before killing scrcpy so it never lingers half-destroyed in the panel.
        ScreenHandle = IntPtr.Zero;
        if (!restarting) IsMirroring = false;
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
        Status = $"Saving {Device.Model}'s {what}…";
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
    private async Task<bool> NeedsAndroidAsync(int apiLevel, string what)
    {
        var api = await _tools.GetApiLevelAsync(Device.Serial);
        if (api == 0 || api >= apiLevel) return false;

        Status = $"{what} needs {AndroidVersions.Name(apiLevel.ToString())} or later, and {Device.Model} has "
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
