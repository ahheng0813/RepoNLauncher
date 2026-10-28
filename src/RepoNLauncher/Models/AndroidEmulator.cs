namespace RepoNLauncher.Models;

/// <summary>An Android emulator (AVD), made by Android Studio or the dashboard.</summary>
public class AndroidEmulator
{
    /// <summary>The AVD's name, which the emulator is started by: Medium_Phone_API_36.1.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The name shown for it: Medium Phone API 36.1.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Android version, image and screen: Android 16 · Google Play · 1080×2400.</summary>
    public string Details { get; set; } = string.Empty;
}

/// <summary>An Android system image installed in the SDK, which an emulator runs.</summary>
public class SystemImage
{
    /// <summary>The image folder relative to the SDK, as an emulator's config names it: system-images\android-36\google_apis_playstore\x86_64\.</summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>android-36</summary>
    public string Platform { get; set; } = string.Empty;

    /// <summary>36, or 36.1 for a minor release.</summary>
    public string ApiLevel { get; set; } = string.Empty;

    /// <summary>google_apis_playstore, google_apis, default...</summary>
    public string TagId { get; set; } = string.Empty;

    /// <summary>Google Play, Google APIs...</summary>
    public string TagDisplay { get; set; } = string.Empty;

    /// <summary>x86_64, arm64-v8a...</summary>
    public string Abi { get; set; } = string.Empty;

    public string Label => AndroidVersions.Name(ApiLevel) is { } version
        ? $"{version} (API {ApiLevel}) · {TagDisplay} · {Abi}"
        : $"API {ApiLevel} · {TagDisplay} · {Abi}";
    public override string ToString() => Label;
}

/// <summary>A screen an emulator can have, like Android Studio's generic devices.</summary>
public class EmulatorDevice(string id, string name, int width, int height, int density, bool isTablet = false)
{
    /// <summary>Android Studio's id for it: medium_phone.</summary>
    public string Id { get; } = id;
    public string Name { get; } = name;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int Density { get; } = density;
    public bool IsTablet { get; } = isTablet;

    public string Label => $"{Name} · {Width}×{Height}";
    public override string ToString() => Label;

    public static IReadOnlyList<EmulatorDevice> All { get; } = new[]
    {
        new EmulatorDevice("small_phone", "Small Phone", 720, 1280, 320),
        new EmulatorDevice("medium_phone", "Medium Phone", 1080, 2400, 420),
        new EmulatorDevice("medium_tablet", "Medium Tablet", 2560, 1600, 320, isTablet: true),
    };
}

public static class AndroidVersions
{
    /// <summary>The Android version an API level is, as people know it: 36 is Android 16. Null for one not listed here.</summary>
    public static string? Name(string apiLevel)
    {
        var major = apiLevel.Split('.')[0];
        return major switch
        {
            "36" => "Android 16",
            "35" => "Android 15",
            "34" => "Android 14",
            "33" => "Android 13",
            "32" => "Android 12L",
            "31" => "Android 12",
            "30" => "Android 11",
            "29" => "Android 10",
            "28" => "Android 9",
            "27" => "Android 8.1",
            "26" => "Android 8",
            _ => null,
        };
    }
}
