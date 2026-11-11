using CommunityToolkit.Mvvm.ComponentModel;

namespace RepoNLauncher.Models;

/// <summary>
/// A device as listed by `adb devices -l` — what scrcpy can mirror. The Phone Screen's list also has the emulators on
/// this PC that aren't running, so Show can start one: those have an <see cref="EmulatorToStart"/> and no serial.
/// </summary>
public partial class AndroidDevice : ObservableObject
{
    public string Serial { get; set; } = string.Empty;

    /// <summary>
    /// The name it's shown by: a phone's model (SM G973F), or an emulator's own name (Medium Phone API 36), since Android
    /// calls every emulator sdk gphone64 x86 64. It can change while listed: another emulator can take the same serial.
    /// </summary>
    [ObservableProperty] private string _model = string.Empty;

    /// <summary>adb connection state: "device" when usable, otherwise e.g. "unauthorized" or "offline".</summary>
    public string State { get; set; } = string.Empty;

    public bool IsReady => State == "device";

    /// <summary>For an emulator on this PC that isn't running: its AVD name, which Show starts it by. Null for a device adb lists.</summary>
    public string? EmulatorToStart { get; init; }

    public bool IsEmulatorToStart => EmulatorToStart != null;

    /// <summary>An emulator to start that's starting, from here or the Emulators window.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Note))]
    private bool _isStarting;

    /// <summary>Beside the name in the list: the serial, or that the emulator isn't running yet.</summary>
    public string Note => !IsEmulatorToStart ? Serial : IsStarting ? "starting…" : "not running";

    public override string ToString() => IsEmulatorToStart ? Model : $"{Model} ({Serial})";
}
