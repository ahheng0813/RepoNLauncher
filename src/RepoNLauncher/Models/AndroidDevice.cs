namespace RepoNLauncher.Models;

/// <summary>A device as listed by `adb devices -l` — what scrcpy can mirror.</summary>
public class AndroidDevice
{
    public string Serial { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;

    /// <summary>adb connection state: "device" when usable, otherwise e.g. "unauthorized" or "offline".</summary>
    public string State { get; set; } = string.Empty;

    public bool IsReady => State == "device";

    public override string ToString() => $"{Model} ({Serial})";
}
