using System.IO;

namespace RepoNLauncher.Services;

/// <summary>Where the Android SDK and its emulators live, found the way Android Studio and its tools find them.</summary>
public static class AndroidSdk
{
    /// <summary>Where the SDK may be, in the order the tools look: the environment, then Android Studio's default.</summary>
    public static IEnumerable<string> CandidateRoots => new[]
        {
            Environment.GetEnvironmentVariable("ANDROID_HOME"),
            Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk"),
        }
        .Where(root => !string.IsNullOrEmpty(root))
        .Select(root => root!);

    /// <summary>The SDK folder, or null when there's none on this PC.</summary>
    public static string? FindRoot() => CandidateRoots.FirstOrDefault(Directory.Exists);

    /// <summary>Where the emulators (AVDs) are defined: one .ini file and one .avd folder each.</summary>
    public static string AvdHome
    {
        get
        {
            var avdHome = Environment.GetEnvironmentVariable("ANDROID_AVD_HOME");
            if (!string.IsNullOrEmpty(avdHome)) return avdHome;

            var userHome = Environment.GetEnvironmentVariable("ANDROID_USER_HOME");
            if (string.IsNullOrEmpty(userHome))
            {
                // The older variable names the folder that holds .android.
                var sdkHome = Environment.GetEnvironmentVariable("ANDROID_SDK_HOME");
                userHome = Path.Combine(
                    string.IsNullOrEmpty(sdkHome) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : sdkHome,
                    ".android");
            }
            return Path.Combine(userHome, "avd");
        }
    }
}
