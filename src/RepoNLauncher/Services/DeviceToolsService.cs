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
public record CopiedFiles(int Files, long Bytes, double Seconds)
{
    /// <summary>"2 files, 5 MB in 0.1 s"</summary>
    public string Summary => $"{Files} {(Files == 1 ? "file" : "files")}, {FileSize.Format(Bytes)} in {Math.Max(0.1, Seconds):0.#} s";
}

/// <summary>A file or folder on a device. A link to a folder counts as a folder.</summary>
public record DeviceFile(string Name, string Path, bool IsFolder, long Size, DateTime Modified);

public static class FileSize
{
    /// <summary>"0 KB", "12 KB", "3.4 MB", "1.25 GB": whole kilobytes at least, as File Explorer shows them.</summary>
    public static string Format(long bytes) => bytes switch
    {
        0 => "0 KB",
        < 1024 * 1024 => $"{Math.Max(1, (bytes + 1023) / 1024)} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };
}

public interface IDeviceToolsService
{
    /// <summary>The device's Android API level (28 is Android 9), or 0 when it can't be read.</summary>
    Task<int> GetApiLevelAsync(string serial, CancellationToken ct = default);

    /// <summary>Saves a PNG of the device's screen, at the screen's full resolution.</summary>
    Task TakeScreenshotAsync(string serial, string path, CancellationToken ct = default);

    /// <summary>
    /// Copies files and folders from this PC into a folder on the device, <see cref="DeviceToolsService.DeviceDownloadFolder"/>
    /// unless another is given, replacing ones with the same names. Null when adb doesn't say how much it copied.
    /// </summary>
    Task<CopiedFiles?> SendFilesAsync(string serial, IReadOnlyList<string> paths, string folder = DeviceToolsService.DeviceDownloadFolder,
        CancellationToken ct = default);

    /// <summary>Copies files and folders from the device into a folder on this PC, replacing ones with the same names.</summary>
    Task<CopiedFiles?> GetFilesAsync(string serial, IReadOnlyList<string> devicePaths, string pcFolder, CancellationToken ct = default);

    /// <summary>Where the device keeps files: its internal storage (/sdcard), then any SD card or USB drive in it.</summary>
    Task<IReadOnlyList<string>> GetStorageFoldersAsync(string serial, CancellationToken ct = default);

    /// <summary>What's in a folder on the device, hidden files included.</summary>
    Task<IReadOnlyList<DeviceFile>> ListFolderAsync(string serial, string folder, CancellationToken ct = default);

    Task CreateFolderAsync(string serial, string path, CancellationToken ct = default);

    /// <summary>Gives a file or folder a new name in the same folder; fails when that name is taken.</summary>
    Task RenameAsync(string serial, string path, string newName, CancellationToken ct = default);

    /// <summary>Deletes files and folders, with everything in them, for good: Android has no recycle bin for adb.</summary>
    Task DeleteAsync(string serial, IReadOnlyList<string> paths, CancellationToken ct = default);

    /// <summary>
    /// Copies a file from the device into <paramref name="destination"/>, reporting the bytes that have come so far, and
    /// returns how many came: none when it can't be read.
    /// </summary>
    Task<long> ReadFileAsync(string serial, string path, Stream destination, IProgress<long>? progress = null, CancellationToken ct = default);

    /// <summary>A file's first bytes, at least <paramref name="count"/> of them unless the file is shorter.</summary>
    Task<byte[]> ReadFileStartAsync(string serial, string path, int count, CancellationToken ct = default);

    /// <summary>The small copy the device's gallery keeps of a picture or video; null when it has none.</summary>
    Task<byte[]?> GetMediaThumbnailAsync(string serial, string path, bool isVideo, CancellationToken ct = default);

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

/// <summary>The Phone screen's tools that only need adb: screenshots, the phone's files and virtual locations.</summary>
public class DeviceToolsService(IScrcpyService scrcpy) : IDeviceToolsService
{
    public const string DeviceDownloadFolder = "/sdcard/Download/";

    // The providers apps get locations from: the GPS, the network's estimate, and Android's mix of both, which most
    // apps use. Replacing only the GPS would leave the others reporting the real location.
    private static readonly string[] LocationProviders = { "gps", "network", "fused" };

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);

    private static readonly Regex CopySummary = new(@"(\d+) files? (?:pushed|pulled), \d+ skipped\..*?\((\d+) bytes in ([\d.]+)s\)", RegexOptions.Compiled);

    // "adb ls" lines, for devices too old to have find and stat: mode, size and time in hex, then the name.
    private static readonly Regex SyncListLine = new(@"^([0-9a-f]{8}) ([0-9a-f]{8}) ([0-9a-f]{8}) (.+)$", RegexOptions.Compiled);

    // Printed after a shell command works: old devices don't pass the command's exit code back.
    private const string Done = "RNL_DONE";

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

    public async Task<CopiedFiles?> SendFilesAsync(string serial, IReadOnlyList<string> paths, string folder = DeviceDownloadFolder,
        CancellationToken ct = default)
    {
        var target = folder.EndsWith('/') ? folder : folder + "/";
        var args = new List<string> { "-s", serial, "push" };
        args.AddRange(paths);
        args.Add(target);

        // Big files take as long as they take, so only the caller can cancel this.
        var result = await RunAdbAsync(ct, Timeout.InfiniteTimeSpan, args.ToArray()).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(CleanAdbError(result.Errors) ?? CleanAdbError(result.Output) ?? "adb couldn't copy the files.");

        await ScanMediaAsync(serial, paths.Where(File.Exists).Select(f => target + Path.GetFileName(f)), ct).ConfigureAwait(false);
        return ReadSummary(result);
    }

    public async Task<CopiedFiles?> GetFilesAsync(string serial, IReadOnlyList<string> devicePaths, string pcFolder, CancellationToken ct = default)
    {
        // -a keeps the files' dates.
        var args = new List<string> { "-s", serial, "pull", "-a" };
        args.AddRange(devicePaths);
        args.Add(pcFolder);

        var result = await RunAdbAsync(ct, Timeout.InfiniteTimeSpan, args.ToArray()).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(CleanAdbError(result.Errors) ?? CleanAdbError(result.Output) ?? "adb couldn't copy the files.");
        return ReadSummary(result);
    }

    public async Task<IReadOnlyList<string>> GetStorageFoldersAsync(string serial, CancellationToken ct = default)
    {
        // /storage has the internal storage twice (emulated, and self for the current user), then a folder for each card.
        var result = await RunAdbAsync(ct, CommandTimeout, "-s", serial, "shell",
            "for d in /storage/*; do [ -d \"$d\" ] && echo \"$d\"; done").ConfigureAwait(false);
        var cards = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(d => d.StartsWith("/storage/", StringComparison.Ordinal) && d is not "/storage/emulated" and not "/storage/self");
        return new[] { "/sdcard" }.Concat(cards).ToList();
    }

    public async Task<IReadOnlyList<DeviceFile>> ListFolderAsync(string serial, string folder, CancellationToken ct = default)
    {
        // stat -L describes what a link points to, so a link to a folder lists as one.
        var result = await RunAdbAsync(ct, CommandTimeout, "-s", serial, "shell",
            $"cd {Quote(folder)} || exit 1; find . -mindepth 1 -maxdepth 1 -exec stat -L -c '%F|%s|%Y|%n' {{}} +; echo {Done}").ConfigureAwait(false);
        if (!result.Output.Contains(Done, StringComparison.Ordinal))
            throw new InvalidOperationException(ShellProblem(result) ?? "the phone didn't list it");

        var files = new List<DeviceFile>();
        foreach (var line in result.Output.Split('\n'))
        {
            var parts = line.TrimEnd('\r').Split('|', 4);
            if (parts.Length != 4 || !long.TryParse(parts[1], out var size) || !long.TryParse(parts[2], out var seconds)) continue;
            var name = parts[3].StartsWith("./", StringComparison.Ordinal) ? parts[3][2..] : parts[3];
            files.Add(new DeviceFile(name, Combine(folder, name), parts[0] == "directory", size,
                DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime));
        }

        // Before Android 7 there's no find or stat to ask; adb's own listing has less to say about links.
        if (files.Count == 0 && (result.Output + result.Errors).Contains("not found", StringComparison.Ordinal))
            return await ListWithAdbAsync(serial, folder, ct).ConfigureAwait(false);
        return files;
    }

    private async Task<IReadOnlyList<DeviceFile>> ListWithAdbAsync(string serial, string folder, CancellationToken ct)
    {
        var result = await RunAdbAsync(ct, CommandTimeout, "-s", serial, "ls", folder.TrimEnd('/') + "/").ConfigureAwait(false);
        var files = new List<DeviceFile>();
        foreach (var line in result.Output.Split('\n'))
        {
            var match = SyncListLine.Match(line.TrimEnd('\r'));
            if (!match.Success || match.Groups[4].Value is "." or "..") continue;
            var mode = Convert.ToUInt32(match.Groups[1].Value, 16) & 0xF000;
            var name = match.Groups[4].Value;
            // A link (0xA000) is taken for a folder: on a phone's storage they're links to folders.
            files.Add(new DeviceFile(name, Combine(folder, name), mode is 0x4000 or 0xA000, Convert.ToInt64(match.Groups[2].Value, 16),
                DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(match.Groups[3].Value, 16)).LocalDateTime));
        }
        return files;
    }

    public async Task CreateFolderAsync(string serial, string path, CancellationToken ct = default) =>
        await RunShellAsync(serial, $"mkdir {Quote(path)}", ct).ConfigureAwait(false);

    public async Task RenameAsync(string serial, string path, string newName, CancellationToken ct = default)
    {
        var newPath = Combine(ParentOf(path), newName);
        // A name that differs only in capitals is the same file on a phone's storage, so it can't be "taken".
        var check = string.Equals(path, newPath, StringComparison.OrdinalIgnoreCase)
            ? ""
            : $"if [ -e {Quote(newPath)} ]; then echo 'File exists' >&2; exit 1; fi; ";
        await RunShellAsync(serial, $"{check}mv {Quote(path)} {Quote(newPath)}", ct).ConfigureAwait(false);
        await ScanMediaAsync(serial, new[] { path, newPath }, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string serial, IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        if (paths.Count == 0) return;
        await RunShellAsync(serial, "rm -rf " + string.Join(' ', paths.Select(Quote)), ct).ConfigureAwait(false);
        await ScanMediaAsync(serial, paths, ct).ConfigureAwait(false);
    }

    // Big files take as long as they take, so only the caller can cancel this.
    public Task<long> ReadFileAsync(string serial, string path, Stream destination, IProgress<long>? progress = null, CancellationToken ct = default) =>
        CopyOutputAsync(serial, $"cat {Quote(path)} 2>/dev/null", destination, Timeout.InfiniteTimeSpan, progress, ct);

    public async Task<byte[]> ReadFileStartAsync(string serial, string path, int count, CancellationToken ct = default)
    {
        // dd, as old devices' head can't count bytes. It counts reads rather than bytes, and a read of a phone's storage
        // can come back short of a big block, but not of a 64 KB one.
        const int block = 64 * 1024;
        using var data = new MemoryStream();
        await CopyOutputAsync(serial, $"dd if={Quote(path)} bs={block} count={(count + block - 1) / block} 2>/dev/null", data,
            CommandTimeout, null, ct).ConfigureAwait(false);
        return data.ToArray();
    }

    public async Task<byte[]?> GetMediaThumbnailAsync(string serial, string path, bool isVideo, CancellationToken ct = default)
    {
        var media = $"content://media/external/{(isVideo ? "video" : "images")}";
        // The gallery knows a file by its real path, and /sdcard is a link (as some of its folders can be). The path goes
        // in the query between SQL's quotes, so the quotes in it are doubled.
        var script = "exec 2>/dev/null; "
                     + $"d=$(cd {Quote(ParentOf(path))} && pwd -P) || exit; "
                     + $"p=$(printf '%s' \"$d/\"{Quote(path[(path.LastIndexOf('/') + 1)..])} | sed \"s/'/''/g\"); "
                     + $"id=$(content query --uri {media}/media --projection _id --where \"_data='$p'\" | sed -n 's/.*_id=\\([0-9]*\\).*/\\1/p' | head -n 1); "
                     + "[ -n \"$id\" ] || exit; "
                     // From Android 10 the gallery makes a thumbnail as it's read. Before, asking where one is waits for it to be made.
                     + "if [ \"$(getprop ro.build.version.sdk)\" -ge 29 ]; then "
                     + $"content read --uri {media}/media/$id/thumbnail; "
                     + $"else t=$(content query --uri \"{media}/thumbnails?blocking=1&orig_id=$id&group_id=0\" --projection _data | sed -n 's/.*_data=//p' | head -n 1); "
                     + "[ -n \"$t\" ] && cat \"$t\"; fi";
        using var data = new MemoryStream();
        await CopyOutputAsync(serial, script, data, CommandTimeout, null, ct).ConfigureAwait(false);
        return data.Length > 0 ? data.ToArray() : null;
    }

    // Runs a command whose output is a file's bytes, copying them as they come. exec-out passes them through untouched,
    // where "shell" would turn line endings into CRLF on some devices; what the command writes to stderr would be mixed
    // in with them, so the commands send that nowhere.
    private async Task<long> CopyOutputAsync(string serial, string command, Stream destination, TimeSpan timeout,
        IProgress<long>? progress, CancellationToken ct)
    {
        using var process = StartAdb(NewAdbStartInfo("-s", serial, "exec-out", command));
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout != Timeout.InfiniteTimeSpan) limit.CancelAfter(timeout);

        var errors = process.StandardError.ReadToEndAsync(limit.Token);
        var buffer = new byte[81920];
        long copied = 0, reported = 0;
        try
        {
            int read;
            while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer, limit.Token).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), limit.Token).ConfigureAwait(false);
                copied += read;
                // A megabyte at a time is often enough to move a progress bar.
                if (progress != null && copied - reported >= 1024 * 1024) progress.Report(reported = copied);
            }
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException("The device didn't answer in time. Is it still connected?");
        }

        // adb's own problems, like the device having gone, are on its stderr.
        if (copied == 0 && process.ExitCode != 0)
            throw new InvalidOperationException(CleanAdbError(await errors.ConfigureAwait(false)) ?? "adb couldn't read the file.");
        progress?.Report(copied);
        return copied;
    }

    // Runs a command that prints nothing when it works, and throws with what it printed when it doesn't.
    private async Task RunShellAsync(string serial, string command, CancellationToken ct)
    {
        var result = await RunAdbAsync(ct, CommandTimeout, "-s", serial, "shell", $"{command} && echo {Done}").ConfigureAwait(false);
        if (!result.Output.Contains(Done, StringComparison.Ordinal))
            throw new InvalidOperationException(ShellProblem(result) ?? "the phone said no");
    }

    // From Android 11 the media library notices changed files by itself. Before that, photos and videos only show in the
    // gallery (or leave it) once they're scanned.
    private async Task ScanMediaAsync(string serial, IEnumerable<string> devicePaths, CancellationToken ct)
    {
        var paths = devicePaths.ToList();
        if (paths.Count == 0 || await GetApiLevelAsync(serial, ct).ConfigureAwait(false) is not (> 0 and < 30)) return;

        var scans = paths.Select(p => "am broadcast -a android.intent.action.MEDIA_SCANNER_SCAN_FILE -d "
                                      + Quote("file://" + string.Join('/', p.Split('/').Select(Uri.EscapeDataString))) + " > /dev/null");
        await RunAdbAsync(ct, CommandTimeout, "-s", serial, "shell", string.Join("; ", scans)).ConfigureAwait(false);
    }

    // Each file gets a line like the total, which comes last ("photo.jpg: 1 file pushed, …", then "2 files pushed, …").
    // adb writes them to stderr when it isn't writing to a terminal.
    private static CopiedFiles? ReadSummary((int ExitCode, string Output, string Errors) result)
    {
        var summary = CopySummary.Matches(result.Errors + "\n" + result.Output).LastOrDefault();
        return summary != null
            ? new CopiedFiles(int.Parse(summary.Groups[1].Value), long.Parse(summary.Groups[2].Value),
                double.Parse(summary.Groups[3].Value, CultureInfo.InvariantCulture))
            : null;
    }

    // What a failed file command said, the way a person would: "/system/bin/sh: cd: /sdcard/x: No such file or directory",
    // "rm: /storage/1A2B-3C4D/x: Read-only file system" and the like.
    private static string? ShellProblem((int ExitCode, string Output, string Errors) result)
    {
        var line = (result.Errors + "\n" + result.Output).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l != Done);
        if (line == null) return null;
        var reason = line[(line.LastIndexOf(": ", StringComparison.Ordinal) + 1)..].Trim();
        return reason switch
        {
            "No such file or directory" => "it isn't there any more",
            "Permission denied" or "Operation not permitted" => "Android doesn't let adb change things there",
            "File exists" => "there's already something with that name there",
            "Read-only file system" => "that storage is read-only",
            "No space left on device" => "the phone is full",
            "Not a directory" => "that isn't a folder",
            _ => reason,
        };
    }

    /// <summary>A path quoted for the device's shell, whatever is in it.</summary>
    private static string Quote(string text) => "'" + text.Replace("'", "'\\''") + "'";

    private static string Combine(string folder, string name) => folder.TrimEnd('/') + "/" + name;

    private static string ParentOf(string path)
    {
        var trimmed = path.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash <= 0 ? "/" : trimmed[..slash];
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
