namespace RepoNLauncher.Models;

/// <summary>One of the device's own buttons, pressed from the Phone screen's tools. Each is its Android key code.</summary>
public enum DeviceKey
{
    Home = 3,
    Back = 4,

    /// <summary>The power button: turns the screen off, or on again (to the lock screen, when the phone has one).</summary>
    Power = 26,

    /// <summary>The recent apps, to switch between them.</summary>
    Recents = 187,
}
