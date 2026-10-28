using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using RepoNLauncher.Models;

namespace RepoNLauncher.Services;

public interface IEmulatorService
{
    /// <summary>The Android SDK folder, or null when there's none on this PC.</summary>
    string? SdkRoot { get; }

    /// <summary>The SDK has the Android Emulator, which runs the emulators.</summary>
    bool HasEmulator { get; }

    List<AndroidEmulator> GetEmulators();

    /// <summary>Installed system images this PC can run, newest Android first.</summary>
    List<SystemImage> GetSystemImages();

    /// <summary>The AVD name a display name gets: letters, digits, dots, dashes and underscores.</summary>
    string NameFor(string displayName);

    bool Exists(string name);

    /// <summary>Defines a new emulator the way Android Studio does; the emulator builds its data on the first start.</summary>
    void Create(string displayName, EmulatorDevice device, SystemImage image);

    /// <summary>The running emulators, by AVD name, with their adb serial (emulator-5554).</summary>
    Task<Dictionary<string, string>> GetRunningAsync(CancellationToken ct = default);

    /// <summary>
    /// Starts it in its own window and waits until Android has booted. It keeps running when the dashboard closes, as
    /// one started from Android Studio does. Throws with the emulator's own message when it doesn't start.
    /// </summary>
    Task StartAsync(string name, IProgress<string> progress, CancellationToken ct = default);
}

public class EmulatorService : IEmulatorService
{
    private static readonly TimeSpan BootTimeout = TimeSpan.FromMinutes(6);
    private static readonly Regex InvalidNameChars = new(@"[^A-Za-z0-9._-]+", RegexOptions.Compiled);

    public string? SdkRoot => AndroidSdk.FindRoot();

    private string? EmulatorExe => SdkRoot is { } root && File.Exists(Path.Combine(root, "emulator", "emulator.exe"))
        ? Path.Combine(root, "emulator", "emulator.exe")
        : null;

    private string? AdbExe => SdkRoot is { } root && File.Exists(Path.Combine(root, "platform-tools", "adb.exe"))
        ? Path.Combine(root, "platform-tools", "adb.exe")
        : null;

    public bool HasEmulator => EmulatorExe != null;

    public List<AndroidEmulator> GetEmulators()
    {
        var emulators = new List<AndroidEmulator>();
        var home = AndroidSdk.AvdHome;
        if (!Directory.Exists(home)) return emulators;

        foreach (var ini in Directory.EnumerateFiles(home, "*.ini"))
        {
            var name = Path.GetFileNameWithoutExtension(ini);
            var path = ReadProperties(ini).GetValueOrDefault("path");
            var configPath = path == null ? null : Path.Combine(path, "config.ini");
            if (configPath == null || !File.Exists(configPath)) continue;

            var config = ReadProperties(configPath);
            var target = config.GetValueOrDefault("target") ?? string.Empty;
            var api = target.StartsWith("android-") ? target["android-".Length..] : target;
            var details = new List<string> { AndroidVersions.Name(api) ?? $"API {api}" };
            if (config.GetValueOrDefault("tag.display") is { Length: > 0 } tag) details.Add(tag);
            if (config.GetValueOrDefault("hw.lcd.width") is { } width && config.GetValueOrDefault("hw.lcd.height") is { } height)
                details.Add($"{width}×{height}");

            emulators.Add(new AndroidEmulator
            {
                Name = name,
                DisplayName = config.GetValueOrDefault("avd.ini.displayname") is { Length: > 0 } display ? display : name.Replace('_', ' '),
                Details = string.Join(" · ", details),
            });
        }
        return emulators.OrderBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public List<SystemImage> GetSystemImages()
    {
        var images = new List<SystemImage>();
        if (SdkRoot is not { } root || !Directory.Exists(Path.Combine(root, "system-images"))) return images;

        // An emulator runs images for this PC's own kind of processor: x86_64 (and x86) here, arm64 on an ARM PC.
        var runnable = RuntimeInformation.OSArchitecture == Architecture.Arm64
            ? new[] { "arm64-v8a" }
            : new[] { "x86_64", "x86" };

        // system-images\<platform>\<tag>\<abi>\source.properties
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "system-images"), "source.properties", SearchOption.AllDirectories))
        {
            var dir = Path.GetDirectoryName(file)!;
            var parts = Path.GetRelativePath(root, dir).Split(Path.DirectorySeparatorChar);
            if (parts.Length != 4) continue;

            var props = ReadProperties(file);
            var abi = props.GetValueOrDefault("SystemImage.Abi") ?? parts[3];
            if (!runnable.Contains(abi)) continue;

            images.Add(new SystemImage
            {
                RelativePath = string.Join('\\', parts) + "\\",
                Platform = parts[1],
                ApiLevel = props.GetValueOrDefault("AndroidVersion.ApiLevel") ?? parts[1].Replace("android-", ""),
                TagId = props.GetValueOrDefault("SystemImage.TagId") ?? parts[2],
                TagDisplay = props.GetValueOrDefault("SystemImage.TagDisplay") ?? parts[2],
                Abi = abi,
            });
        }
        return images
            .OrderByDescending(i => Version.TryParse(i.ApiLevel.Contains('.') ? i.ApiLevel : i.ApiLevel + ".0", out var v) ? v : new Version())
            .ThenBy(i => i.TagId == "google_apis_playstore" ? 0 : 1)
            .ToList();
    }

    public string NameFor(string displayName) => InvalidNameChars.Replace(displayName.Trim(), "_").Trim('_');

    public bool Exists(string name)
    {
        var home = AndroidSdk.AvdHome;
        return File.Exists(Path.Combine(home, name + ".ini")) || Directory.Exists(Path.Combine(home, name + ".avd"));
    }

    public void Create(string displayName, EmulatorDevice device, SystemImage image)
    {
        var name = NameFor(displayName);
        if (name.Length == 0) throw new ArgumentException("Give the emulator a name.");
        if (Exists(name)) throw new InvalidOperationException($"There's already an emulator called {name}.");

        var home = AndroidSdk.AvdHome;
        var avdDir = Path.Combine(home, name + ".avd");
        Directory.CreateDirectory(avdDir);

        // The settings Android Studio writes for its generic devices. Android builds the emulator's data from the image on
        // the first start, so nothing else is needed.
        var config = new (string Key, string Value)[]
        {
            ("AvdId", name),
            ("PlayStore.enabled", image.TagId == "google_apis_playstore" ? "true" : "false"),
            ("abi.type", image.Abi),
            ("avd.ini.displayname", displayName.Trim()),
            ("avd.ini.encoding", "UTF-8"),
            ("disk.dataPartition.size", "6G"),
            ("fastboot.forceColdBoot", "no"),
            ("fastboot.forceFastBoot", "yes"),
            ("hw.accelerometer", "yes"),
            ("hw.audioInput", "yes"),
            ("hw.battery", "yes"),
            ("hw.camera.back", "virtualscene"),
            ("hw.camera.front", "emulated"),
            ("hw.cpu.arch", image.Abi switch { "arm64-v8a" => "arm64", "armeabi-v7a" => "arm", _ => image.Abi }),
            ("hw.cpu.ncore", Math.Clamp(Environment.ProcessorCount / 2, 2, 4).ToString()),
            ("hw.dPad", "no"),
            ("hw.device.manufacturer", "Generic"),
            ("hw.device.name", device.Id),
            ("hw.gps", "yes"),
            ("hw.gpu.enabled", "yes"),
            ("hw.gpu.mode", "auto"),
            ("hw.gyroscope", "yes"),
            ("hw.initialOrientation", device.IsTablet ? "landscape" : "portrait"),
            ("hw.keyboard", "yes"),
            ("hw.lcd.density", device.Density.ToString()),
            ("hw.lcd.height", device.Height.ToString()),
            ("hw.lcd.width", device.Width.ToString()),
            ("hw.mainKeys", "no"),
            ("hw.ramSize", "2048"),
            ("hw.sdCard", "no"),
            ("hw.sensors.orientation", "yes"),
            ("hw.sensors.proximity", "yes"),
            ("hw.trackBall", "no"),
            ("image.sysdir.1", image.RelativePath),
            ("runtime.network.latency", "none"),
            ("runtime.network.speed", "full"),
            ("showDeviceFrame", "yes"),
            ("skin.dynamic", "yes"),
            ("skin.name", $"{device.Width}x{device.Height}"),
            ("skin.path", $"{device.Width}x{device.Height}"),
            ("tag.display", image.TagDisplay),
            ("tag.id", image.TagId),
            ("target", image.Platform),
        };
        WriteProperties(Path.Combine(avdDir, "config.ini"), config);

        WriteProperties(Path.Combine(home, name + ".ini"), new[]
        {
            ("avd.ini.encoding", "UTF-8"),
            ("path", avdDir),
            ("path.rel", Path.Combine("avd", name + ".avd")),
            ("target", image.Platform),
        });
    }

    public async Task<Dictionary<string, string>> GetRunningAsync(CancellationToken ct = default)
    {
        var running = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (AdbExe == null) return running;

        var devices = await RunAdbAsync("devices", ct);
        var serials = devices.Split('\n')
            .Select(l => l.Split('\t', StringSplitOptions.TrimEntries))
            .Where(p => p.Length == 2 && p[0].StartsWith("emulator-"))
            .Select(p => p[0]);

        foreach (var serial in serials)
        {
            // Prints the AVD name, then OK.
            var name = (await RunAdbAsync($"-s {serial} emu avd name", ct)).Split('\n')[0].Trim();
            if (name.Length > 0 && name != "OK") running[name] = serial;
        }
        return running;
    }

    public async Task StartAsync(string name, IProgress<string> progress, CancellationToken ct = default)
    {
        var emulator = EmulatorExe ?? throw new InvalidOperationException("The Android Emulator isn't installed.");
        if (AdbExe == null) throw new InvalidOperationException("The Android SDK's platform tools (adb) aren't installed.");

        // The emulator's own output goes to a file: a pipe to the dashboard would break when the dashboard closes, and
        // the file says what went wrong when it doesn't start.
        var logDir = Path.Combine(Path.GetTempPath(), "RepoNLauncher");
        Directory.CreateDirectory(logDir);
        var log = Path.Combine(logDir, $"emulator-{name}.log");

        var info = new ProcessStartInfo("cmd.exe", $"/c \"\"{emulator}\" -avd {name} > \"{log}\" 2>&1\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.Environment["ANDROID_HOME"] = SdkRoot;
        info.Environment["ANDROID_SDK_ROOT"] = SdkRoot;
        info.Environment["ANDROID_AVD_HOME"] = AndroidSdk.AvdHome;
        using var process = Process.Start(info)!;

        progress.Report("Starting the emulator…");
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < BootTimeout)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            if (process.HasExited) throw new InvalidOperationException(LastError(log));

            if (!(await GetRunningAsync(ct)).TryGetValue(name, out var serial)) continue;
            progress.Report("Android is starting up…");
            if ((await RunAdbAsync($"-s {serial} shell getprop sys.boot_completed", ct)).Trim() == "1") return;
        }
        throw new TimeoutException($"Android didn't finish starting within {BootTimeout.TotalMinutes:0} minutes. It may still be starting in the emulator's window.");
    }

    // The emulator ends its output with what went wrong, e.g. that this PC can't run emulators.
    private static string LastError(string log)
    {
        var lines = File.Exists(log)
            ? File.ReadAllLines(log).Select(l => l.Trim()).Where(l => l.Length > 0).ToList()
            : new List<string>();
        var error = lines.LastOrDefault(l => l.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || l.Contains("FATAL", StringComparison.OrdinalIgnoreCase))
                    ?? lines.LastOrDefault();
        return error == null
            ? "The emulator closed while starting."
            : Regex.Replace(error, @"^(ERROR|FATAL|\w+\s+\|)\s*[:|]?\s*", "", RegexOptions.IgnoreCase);
    }

    private async Task<string> RunAdbAsync(string arguments, CancellationToken ct)
    {
        var info = new ProcessStartInfo(AdbExe!, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(info)!;
        // adb can hang on a device that's still coming up; give up on it rather than on the whole start.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return output;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(); } catch { /* already exited */ }
            return string.Empty;
        }
    }

    private static Dictionary<string, string> ReadProperties(string path)
    {
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(path))
        {
            var eq = line.IndexOf('=');
            if (eq > 0) props[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return props;
    }

    private static void WriteProperties(string path, IEnumerable<(string Key, string Value)> props) =>
        File.WriteAllText(path, string.Concat(props.Select(p => $"{p.Key}={p.Value}\n")), new UTF8Encoding(false));
}
