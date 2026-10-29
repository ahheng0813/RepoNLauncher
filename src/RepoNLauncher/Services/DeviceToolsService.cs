using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using RepoNLauncher.Models;

namespace RepoNLauncher.Services;

/// <summary>How a device was given a virtual location.</summary>
public enum VirtualLocationKind
{
    /// <summary>An Android Studio emulator's GPS was moved there; it has no real location to go back to.</summary>
    EmulatorGps,

    /// <summary>A phone's location providers were replaced with test ones that report it, until they're removed.</summary>
    PhoneTestProviders,
}

public record VirtualLocationResult(VirtualLocationKind Kind, bool IsLocationOff);

/// <summary>What adb says it copied, when it says: "2 files pushed, 0 skipped. 38.1 MB/s (5242880 bytes in 0.131s)".</summary>
public record SentFiles(int Files, long Bytes, double Seconds);

public interface IDeviceToolsService
{
    /// <summary>The device's Android API level (28 is Android 9), or 0 when it can't be read.</summary>
    Task<int> GetApiLevelAsync(string serial, CancellationToken ct = default);

    /// <summary>Saves a PNG of the device's screen, at the screen's full resolution.</summary>
    Task TakeScreenshotAsync(string serial, string path, CancellationToken ct = default);

    /// <summary>
    /// Copies files and folders to <see cref="DeviceToolsService.DeviceDownloadFolder"/>, where the device's Files app
    /// shows them. Null when adb doesn't say how much it copied.
    /// </summary>
    Task<SentFiles?> SendFilesAsync(string serial, IReadOnlyList<string> paths, CancellationToken ct = default);

    /// <summary>
    /// Moves the device to a virtual location. An Android Studio emulator's GPS goes there. A phone (or another
    /// emulator) on Android 12 or later gets test location providers that report it, which apps see until
    /// <see cref="ClearPhoneLocationAsync"/>; it needs reporting again now and then, see <see cref="RefreshPhoneLocationAsync"/>.
    /// </summary>
    Task<VirtualLocationResult> SetLocationAsync(string serial, double latitude, double longitude, CancellationToken ct = default);

    /// <summary>Reports a phone's virtual location again: an app waiting for a new position only gets one when it's reported.</summary>
    Task RefreshPhoneLocationAsync(string serial, double latitude, double longitude, CancellationToken ct = default);

    /// <summary>Removes a phone's test location providers, so it reports where it really is again.</summary>
    Task ClearPhoneLocationAsync(string serial, CancellationToken ct = default);
}

/// <summary>The Phone screen's tools that only need adb: screenshots, sending files and virtual locations.</summary>
public class DeviceToolsService(IScrcpyService scrcpy) : IDeviceToolsService
{
    public const string DeviceDownloadFolder = "/sdcard/Download/";

    // The providers apps get locations from: the GPS, the network's estimate, and Android's mix of both, which most
    // apps use. Replacing only the GPS would leave the others reporting the real location.
    private static readonly string[] LocationProviders = { "gps", "network", "fused" };

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);

    private static readonly Regex PushSummary = new(@"(\d+) files? pushed, \d+ skipped\..*?\((\d+) bytes in ([\d.]+)s\)", RegexOptions.Compiled);

    public async Task<int> GetApiLevelAsync(string serial, CancellationToken ct = default)
    {
        var result = await RunAdbAsync(ct, CommandTimeout, "-s", serial, "shell", "getprop", "ro.build.version.sdk").ConfigureAwait(false);
        return int.TryParse(result.Output.Trim(), out var api) ? api : 0;
    }

    public async Task TakeScreenshotAsync(string serial, string path, CancellationToken ct = default)
    {
        // exec-out passes the PNG through untouched; "shell" would turn its line endings into CRLF on some devices.
        var info = NewAdbStartInfo("-s", serial, "exec-out", "screencap", "-p");
        using var process = StartAdb(info);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CommandTimeout);

        using var png = new MemoryStream();
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.StandardOutput.BaseStream.CopyToAsync(png, timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Kill(process);
            throw new TimeoutException("The device didn't send its screen in time.");
        }

        var bytes = png.ToArray();
        if (bytes.Length < 8 || bytes[0] != 0x89 || bytes[1] != (byte)'P' || bytes[2] != (byte)'N' || bytes[3] != (byte)'G')
            throw new InvalidOperationException(FirstLine(await errors.ConfigureAwait(false)) ?? "The device didn't send a picture of its screen.");

        await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
    }

    public async Task<SentFiles?> SendFilesAsync(string serial, IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        var args = new List<string> { "-s", serial, "push" };
        args.AddRange(paths);
        args.Add(DeviceDownloadFolder);

        // Big files take as long as they take, so only the caller can cancel this.
        var result = await RunAdbAsync(ct, Timeout.InfiniteTimeSpan, args.ToArray()).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(CleanAdbError(result.Errors) ?? CleanAdbError(result.Output) ?? "adb couldn't copy the files.");

        // From Android 11 the media library notices new files by itself. Before that, photos and videos only show in the
        // gallery once they're scanned.
        var files = paths.Where(File.Exists).ToList();
        if (files.Count > 0 && await GetApiLevelAsync(serial, ct).ConfigureAwait(false) is > 0 and < 30)
        {
            var scans = files.Select(f =>
                $"am broadcast -a android.intent.action.MEDIA_SCANNER_SCAN_FILE -d 'file://{DeviceDownloadFolder}{Uri.EscapeDataString(Path.GetFileName(f))}' > /dev/null");
            await RunAdbAsync(ct, CommandTimeout, "-s", serial, "shell", string.Join("; ", scans)).ConfigureAwait(false);
        }

        // Each file gets a line like the total, which comes last ("photo.jpg: 1 file pushed, …", then "2 files pushed, …").
        // adb writes them to stderr when it isn't writing to a terminal.
        var summary = PushSummary.Matches(result.Errors + "\n" + result.Output).LastOrDefault();
        return summary != null
            ? new SentFiles(int.Parse(summary.Groups[1].Value), long.Parse(summary.Groups[2].Value),
                double.Parse(summary.Groups[3].Value, CultureInfo.InvariantCulture))
            : null;
    }

    public async Task<VirtualLocationResult> SetLocationAsync(string serial, double latitude, double longitude, CancellationToken ct = default)
    {
        // Android Studio's emulators have a console that moves their GPS (longitude first). Other emulators use the same
        // kind of serial but have no console, so they're treated like phones.
        if (serial.StartsWith("emulator-", StringComparison.Ordinal))
        {
            var fix = await RunAdbAsync(ct, CommandTimeout, "-s", serial, "emu", "geo", "fix", Format(longitude), Format(latitude)).ConfigureAwait(false);
            if (fix.Output.Split('\n').Any(line => line.Trim() == "OK")) return new VirtualLocationResult(VirtualLocationKind.EmulatorGps, false);
        }

        var api = await GetApiLevelAsync(serial, ct).ConfigureAwait(false);
        if (api is > 0 and < 31)
            throw new InvalidOperationException(
                $"setting a location on it needs Android 12 or later, and it has {AndroidVersions.Name(api.ToString()) ?? $"API {api}"}");

        // The shell may only add test providers once it's allowed to mock locations, as the mock location app in
        // Developer options is.
        var script = new StringBuilder("appops set com.android.shell android:mock_location allow");
        foreach (var provider in LocationProviders)
        {
            script.Append($"; cmd location providers add-test-provider {provider}");
            script.Append($"; cmd location providers set-test-provider-enabled {provider} true");
        }
        script.Append("; ").Append(ReportLocationScript(latitude, longitude));
        script.Append("; cmd location is-location-enabled");

        var result = await RunAdbAsync(ct, CommandTimeout, "-s", serial, "shell", script.ToString()).ConfigureAwait(false);
        if (ShellError(result) is { } error) throw new InvalidOperationException(error);

        var locationOff = result.Output.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim() == "false";
        return new VirtualLocationResult(VirtualLocationKind.PhoneTestProviders, locationOff);
    }

    public async Task RefreshPhoneLocationAsync(string serial, double latitude, double longitude, CancellationToken ct = default)
    {
        var result = await RunAdbAsync(ct, CommandTimeout, "-s", serial, "shell", ReportLocationScript(latitude, longitude)).ConfigureAwait(false);
        if (ShellError(result) is { } error) throw new InvalidOperationException(error);
    }

    public async Task ClearPhoneLocationAsync(string serial, CancellationToken ct = default)
    {
        var script = string.Join("; ", LocationProviders.Select(p => $"cmd location providers remove-test-provider {p}"))
                     + "; appops set com.android.shell android:mock_location default";
        var result = await RunAdbAsync(ct, CommandTimeout, "-s", serial, "shell", script).ConfigureAwait(false);
        if (result.ExitCode != 0 && CleanAdbError(result.Errors) is { } error) throw new InvalidOperationException(error);
    }

    // An accuracy of a few metres, like a good GPS fix: some apps ignore positions as vague as the 100 m default.
    private static string ReportLocationScript(double latitude, double longitude) => string.Join("; ", LocationProviders.Select(p =>
        $"cmd location providers set-test-provider-location {p} --location {Format(latitude)},{Format(longitude)} --accuracy 3"));

    private static string Format(double degrees) => degrees.ToString("0.######", CultureInfo.InvariantCulture);

    // The location commands print an exception (or that the command is unknown) and carry on with the next one.
    private static string? ShellError((int ExitCode, string Output, string Errors) result)
    {
        var lines = (result.Output + "\n" + result.Errors).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var problem = lines.FirstOrDefault(l => l.Contains("Exception", StringComparison.Ordinal)
                                                || l.StartsWith("Unknown command", StringComparison.OrdinalIgnoreCase)
                                                || l.StartsWith("adb: ", StringComparison.Ordinal)
                                                || l.StartsWith("error:", StringComparison.OrdinalIgnoreCase));
        if (problem == null) return null;

        // e.g. "Exception occurred while executing 'providers':" then "java.lang.SecurityException: ... needs ..."
        var detail = lines.FirstOrDefault(l => l.StartsWith("java.lang.", StringComparison.Ordinal)) ?? problem;
        var colon = detail.IndexOf(": ", StringComparison.Ordinal);
        return colon > 0 && detail.StartsWith("java.lang.", StringComparison.Ordinal) ? detail[(colon + 2)..] : CleanAdbError(detail);
    }

    // "adb: error: failed to stat remote object" → "failed to stat remote object"
    private static string? CleanAdbError(string text)
    {
        var line = FirstLine(text);
        if (line == null) return null;
        foreach (var prefix in new[] { "adb: error: ", "adb: ", "error: " })
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) line = line[prefix.Length..];
        return line;
    }

    private static string? FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    private async Task<(int ExitCode, string Output, string Errors)> RunAdbAsync(CancellationToken ct, TimeSpan timeout, params string[] args)
    {
        using var process = StartAdb(NewAdbStartInfo(args));
        // adb can hang on a device that's just been unplugged; give up on it rather than wait forever.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout != Timeout.InfiniteTimeSpan) limit.CancelAfter(timeout);
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(limit.Token);
            var errors = process.StandardError.ReadToEndAsync(limit.Token);
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            return (process.ExitCode, await output.ConfigureAwait(false), await errors.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException("The device didn't answer in time. Is it still connected?");
        }
    }

    private ProcessStartInfo NewAdbStartInfo(params string[] args)
    {
        var info = new ProcessStartInfo(scrcpy.FindAdb())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return info;
    }

    private static Process StartAdb(ProcessStartInfo info)
    {
        try
        {
            return Process.Start(info)!;
        }
        catch (Win32Exception)
        {
            throw new InvalidOperationException("adb wasn't found. Install scrcpy from the Phone screen, or Android Studio.");
        }
    }

    private static void Kill(Process process)
    {
        try { process.Kill(); } catch { /* already exited */ }
    }
}
