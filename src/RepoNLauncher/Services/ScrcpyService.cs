using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RepoNLauncher.Models;

namespace RepoNLauncher.Services;

/// <summary>A running scrcpy process mirroring one device, recording it, or both.</summary>
public class ScrcpySession
{
    private const uint CtrlCEvent = 0;

    // e.g. "INFO: Texture (D3D11VA): 1024x576", printed again when the phone rotates.
    private static readonly Regex TextureLine = new(@"^INFO: Texture\b[^:]*: (\d+)x(\d+)", RegexOptions.Compiled);

    // Only one console can be joined at a time, so sending Ctrl+C to two sessions at once takes turns.
    private static readonly SemaphoreSlim ConsoleGate = new(1, 1);

    private readonly Process _process;
    private readonly string? _windowTitle;
    private string? _lastError;
    private string? _deviceError;

    public string Serial { get; }

    public MirrorSource Source { get; }

    /// <summary>The file it records to, or null when it only mirrors.</summary>
    public string? RecordPath { get; }

    /// <summary>
    /// Why it exited, usually: the error the phone reported (e.g. that it's too old for camera mirroring), or else the
    /// last ERROR line scrcpy printed.
    /// </summary>
    public string? LastError => _deviceError ?? _lastError;

    /// <summary>The last WARN line scrcpy printed, e.g. that audio isn't supported on the device.</summary>
    public string? LastWarning { get; private set; }

    /// <summary>The size of the video it shows, once the first frame has arrived; the window letterboxes it.</summary>
    public (int Width, int Height)? VideoSize { get; private set; }

    /// <summary>Raised on a worker thread when the scrcpy process ends, for whatever reason.</summary>
    public event Action? Exited;

    internal ScrcpySession(Process process, string serial, string? windowTitle, MirrorSource source, string? recordPath)
    {
        _process = process;
        _windowTitle = windowTitle;
        Serial = serial;
        Source = source;
        RecordPath = recordPath;

        _process.OutputDataReceived += (_, e) => TrackProblem(e.Data);
        _process.ErrorDataReceived += (_, e) => TrackProblem(e.Data);
        _process.Exited += (_, _) =>
        {
            // Exited can fire before the last output lines are read; this waits for them, so LastError is final.
            _process.WaitForExit();
            Exited?.Invoke();
        };
    }

    public bool HasExited
    {
        get
        {
            try { return _process.HasExited; }
            catch { return true; }
        }
    }

    internal IntPtr ProcessHandle => _process.Handle;

    internal void Start()
    {
        // scrcpy inherits whether Ctrl+C is ignored, and must not, or its recording can only be cut off. It's ignored here
        // for a moment while one is sent, and the dashboard may have been started with it ignored too.
        ConsoleGate.Wait();
        try
        {
            SetConsoleCtrlHandler(IntPtr.Zero, false);
            _process.Start();
        }
        finally
        {
            ConsoleGate.Release();
        }
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    private void TrackProblem(string? line)
    {
        if (line == null) return;

        // The phone's own messages come first and say what went wrong; scrcpy's that follow only say that it then failed.
        const string server = "[server] ";
        var fromDevice = line.StartsWith(server, StringComparison.Ordinal);
        if (fromDevice) line = line[server.Length..];

        if (line.StartsWith("ERROR:", StringComparison.Ordinal))
        {
            var error = line["ERROR:".Length..].Trim();
            if (fromDevice) _deviceError ??= error;
            else _lastError = error;
        }
        else if (line.StartsWith("WARN:", StringComparison.Ordinal))
        {
            LastWarning = line["WARN:".Length..].Trim();
        }
        else if (TextureLine.Match(line) is { Success: true } texture)
        {
            VideoSize = (int.Parse(texture.Groups[1].Value), int.Parse(texture.Groups[2].Value));
        }
    }

    /// <summary>
    /// Polls until scrcpy has shown its first frame, returning the window handle,
    /// or IntPtr.Zero if the process exits or the timeout passes first.
    /// </summary>
    public async Task<IntPtr> WaitForWindowAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !HasExited)
        {
            var hwnd = FindOwnWindow();
            if (hwnd != IntPtr.Zero) return hwnd;
            await Task.Delay(30); // short, since the shown window sits off-screen as a top-level window until it's adopted
        }
        return IntPtr.Zero;
    }

    private IntPtr FindOwnWindow()
    {
        int pid;
        try { pid = _process.Id; }
        catch (InvalidOperationException) { return IntPtr.Zero; }

        var found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var windowPid);
            if (windowPid != pid) return true;

            // scrcpy creates its window hidden and shows it once the first frame is laid out. Re-parenting it
            // before then makes scrcpy crash (divide by zero) or hang without ever drawing, so wait for it.
            if (!IsWindowVisible(hwnd)) return true;

            var title = new StringBuilder(GetWindowTextLength(hwnd) + 1);
            GetWindowText(hwnd, title, title.Capacity);
            if (title.ToString() != _windowTitle) return true;

            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    public void Stop()
    {
        try
        {
            if (!_process.HasExited) _process.Kill();
        }
        catch { /* already exited */ }
    }

    /// <summary>
    /// Ends it the way Ctrl+C does in a terminal, which lets scrcpy finish its recording: an .mp4 that's cut off has no
    /// index and won't play. Kills it if it hasn't ended within the timeout. True when it ended by itself.
    /// </summary>
    public async Task<bool> FinishAsync(TimeSpan timeout)
    {
        if (HasExited) return true;

        var sent = false;
        await ConsoleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // The dashboard has no console of its own, so it can join scrcpy's (hidden) one and press Ctrl+C there.
            // The press reaches every process on that console, this one included, so it's ignored here meanwhile.
            if (AttachConsole(_process.Id))
            {
                SetConsoleCtrlHandler(IntPtr.Zero, true);
                sent = GenerateConsoleCtrlEvent(CtrlCEvent, 0);
                FreeConsole();
                // The press reaches this process a moment later, on a thread of its own; only then can Ctrl+C be
                // handled normally again. Starting scrcpy waits for that (see Start).
                await Task.Delay(300).ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException)
        {
            // exited meanwhile
        }
        finally
        {
            SetConsoleCtrlHandler(IntPtr.Zero, false);
            ConsoleGate.Release();
        }

        if (sent)
        {
            using var wait = new CancellationTokenSource(timeout);
            try { await _process.WaitForExitAsync(wait.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* killed below */ }
        }

        var finished = HasExited;
        Stop();
        return finished;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);

    [DllImport("kernel32.dll")]
    private static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);
}

public interface IScrcpyService
{
    /// <summary>Full path to scrcpy.exe, or null when it isn't installed.</summary>
    string? FindScrcpy();

    /// <summary>The adb that scrcpy uses: Android Studio's when there is one, so two adb versions don't fight.</summary>
    string FindAdb();

    /// <summary>Every device adb knows about, including ones not yet usable (unauthorized/offline).</summary>
    Task<List<AndroidDevice>> GetDevicesAsync(CancellationToken ct = default);

    /// <summary>
    /// Mirrors the device's screen or a camera in a window, recording it to <paramref name="recordPath"/> as well when
    /// given. For a camera, <paramref name="forwardAudio"/> is whether its recording has the microphone's sound: a camera
    /// has none live, since an emulator's microphone is this PC's and would echo.
    /// </summary>
    ScrcpySession Start(string scrcpyPath, string serial, bool forwardAudio, MirrorSource source = MirrorSource.Screen,
        string? recordPath = null);

    /// <summary>Records the device's microphone to an .m4a file, with no window. Needs Android 11.</summary>
    ScrcpySession StartVoiceRecording(string scrcpyPath, string serial, string path);

    /// <summary>Downloads the latest official Windows build from GitHub into the dashboard's tools folder.</summary>
    Task InstallAsync(IProgress<string> progress, CancellationToken ct = default);
}

/// <summary>
/// Wraps scrcpy (https://github.com/Genymobile/scrcpy), which streams an Android device's screen and audio
/// to the PC and forwards mouse/keyboard input back to it.
/// </summary>
public class ScrcpyService : IScrcpyService
{
    private const string LatestReleaseApi = "https://api.github.com/repos/Genymobile/scrcpy/releases/latest";

    private static readonly string ToolsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RepoNLauncher", "tools", "scrcpy");

    // scrcpy is killed with the dashboard, even on a crash: otherwise it keeps playing the phone's audio
    // after its window is gone. Children may break away so the adb server it might spawn survives.
    private static readonly Lazy<IntPtr> KillOnCloseJob = new(CreateKillOnCloseJob);

    public string? FindScrcpy()
    {
        var path = (Directory.Exists(ToolsDir)
                       ? Directory.EnumerateFiles(ToolsDir, "scrcpy.exe", SearchOption.AllDirectories).FirstOrDefault()
                       : null)
                   ?? FindOnPath("scrcpy.exe");
        if (path == null) return null;

        // winget installs a symlink on the PATH; scrcpy looks for its server file next to the real exe.
        return new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
    }

    public string FindAdb() => FindAdb(FindScrcpy());

    public async Task<List<AndroidDevice>> GetDevicesAsync(CancellationToken ct = default)
    {
        var info = new ProcessStartInfo(FindAdb(), "devices -l")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        string output;
        try
        {
            using var process = Process.Start(info)!;
            output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
        }
        catch (Win32Exception)
        {
            return new List<AndroidDevice>(); // no adb anywhere — nothing can be listed
        }

        // e.g. "R58M1234ABC  device usb:1-1 product:beyond1ltexx model:SM_G973F device:beyond1 transport_id:2"
        var devices = new List<AndroidDevice>();
        foreach (var line in output.Split('\n').Skip(1))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            var model = parts.FirstOrDefault(p => p.StartsWith("model:", StringComparison.Ordinal))?["model:".Length..];
            devices.Add(new AndroidDevice
            {
                Serial = parts[0],
                State = parts[1],
                Model = model?.Replace('_', ' ') ?? parts[0],
            });
        }
        return devices;
    }

    public ScrcpySession Start(string scrcpyPath, string serial, bool forwardAudio, MirrorSource source = MirrorSource.Screen,
        string? recordPath = null)
    {
        var windowTitle = $"Repo'N'Launcher Phone Screen {serial}";
        var info = NewStartInfo(scrcpyPath, serial);
        info.ArgumentList.Add($"--window-title={windowTitle}");
        info.ArgumentList.Add("--window-borderless");
        // The dashboard pulls the window into itself as soon as it's shown; start it off-screen so it never flashes on the desktop.
        info.ArgumentList.Add("--window-x=-32000");
        info.ArgumentList.Add("--window-y=-32000");
        // The mirror is never shown much taller than 1024 px, and a smaller video is cheaper for the phone to encode:
        // measured ~60-90 ms less lag than full resolution. The monitors are 60 Hz at most, so on 120 Hz phones
        // every other frame would be encoded and sent only to be dropped.
        info.ArgumentList.Add("--max-size=1024");
        info.ArgumentList.Add("--max-fps=60");

        if (source == MirrorSource.Screen)
        {
            if (!forwardAudio) info.ArgumentList.Add("--no-audio");
        }
        else
        {
            // For a camera, --max-size picks the largest capture size that fits instead.
            info.ArgumentList.Add("--video-source=camera");
            info.ArgumentList.Add(source == MirrorSource.FrontCamera ? "--camera-facing=front" : "--camera-facing=back");
            info.ArgumentList.Add("--no-control"); // there's nothing to tap
            if (recordPath == null || !forwardAudio)
            {
                info.ArgumentList.Add("--no-audio");
            }
            else
            {
                info.ArgumentList.Add("--audio-source=mic");
                info.ArgumentList.Add("--no-audio-playback");
            }
        }

        if (recordPath != null)
        {
            // Opus, scrcpy's default, doesn't play in Windows' own players; AAC does.
            info.ArgumentList.Add("--audio-codec=aac");
            info.ArgumentList.Add($"--record={recordPath}");
        }

        return Launch(info, serial, windowTitle, source, recordPath);
    }

    public ScrcpySession StartVoiceRecording(string scrcpyPath, string serial, string path)
    {
        var info = NewStartInfo(scrcpyPath, serial);
        info.ArgumentList.Add("--no-video");
        info.ArgumentList.Add("--no-window");
        info.ArgumentList.Add("--no-control");
        info.ArgumentList.Add("--audio-source=mic");
        info.ArgumentList.Add("--audio-codec=aac");
        info.ArgumentList.Add("--require-audio"); // without sound there's nothing to record; fail rather than idle
        info.ArgumentList.Add($"--record={path}");
        return Launch(info, serial, windowTitle: null, MirrorSource.Screen, path);
    }

    private static ProcessStartInfo NewStartInfo(string scrcpyPath, string serial)
    {
        var info = new ProcessStartInfo(scrcpyPath)
        {
            WorkingDirectory = Path.GetDirectoryName(scrcpyPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add($"--serial={serial}");

        // Use the same adb as Flutter/Android Studio: two different adb versions keep killing each other's server.
        info.Environment["ADB"] = FindAdb(scrcpyPath);
        return info;
    }

    private static ScrcpySession Launch(ProcessStartInfo info, string serial, string? windowTitle, MirrorSource source, string? recordPath)
    {
        var session = new ScrcpySession(new Process { StartInfo = info, EnableRaisingEvents = true }, serial, windowTitle, source, recordPath);
        session.Start();
        AssignToKillOnCloseJob(session);
        return session;
    }

    public async Task InstallAsync(IProgress<string> progress, CancellationToken ct = default)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RepoNLauncher"); // the GitHub API rejects requests without one

        progress.Report("Looking up the latest scrcpy release...");
        using var release = JsonDocument.Parse(await http.GetStringAsync(LatestReleaseApi, ct));
        var assets = release.RootElement.GetProperty("assets").EnumerateArray()
            .Select(a => (Name: a.GetProperty("name").GetString() ?? "", Url: a.GetProperty("browser_download_url").GetString() ?? ""))
            .ToList();

        var zip = assets.FirstOrDefault(a => a.Name.StartsWith("scrcpy-win64-", StringComparison.Ordinal) && a.Name.EndsWith(".zip", StringComparison.Ordinal));
        if (zip.Url == "") throw new InvalidOperationException("the latest release has no Windows 64-bit build");

        var zipPath = Path.Combine(Path.GetTempPath(), zip.Name);
        try
        {
            using (var response = await http.GetAsync(zip.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? 0;
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var target = File.Create(zipPath);

                var buffer = new byte[81920];
                long received = 0;
                var lastPercent = -1;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    received += read;
                    var percent = total > 0 ? (int)(received * 100 / total) : 0;
                    if (percent == lastPercent) continue;
                    lastPercent = percent;
                    progress.Report($"Downloading {zip.Name}... {percent}%");
                }
            }

            var sums = assets.FirstOrDefault(a => a.Name == "SHA256SUMS.txt");
            if (sums.Url != "")
            {
                progress.Report("Verifying download...");
                var expected = (await http.GetStringAsync(sums.Url, ct))
                    .Split('\n')
                    .Select(l => l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    .FirstOrDefault(p => p.Length == 2 && p[1] == zip.Name)?[0];
                var actual = await Task.Run(() =>
                {
                    using var stream = File.OpenRead(zipPath);
                    return Convert.ToHexString(SHA256.HashData(stream));
                }, ct);
                if (expected != null && !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("the download is corrupted (checksum mismatch)");
            }

            progress.Report("Extracting...");
            await Task.Run(() =>
            {
                if (Directory.Exists(ToolsDir)) Directory.Delete(ToolsDir, recursive: true);
                ZipFile.ExtractToDirectory(zipPath, ToolsDir);
            }, ct);
        }
        finally
        {
            File.Delete(zipPath);
        }

        if (FindScrcpy() == null) throw new InvalidDataException("scrcpy.exe was not found in the downloaded archive");
    }

    private static string FindAdb(string? scrcpyPath)
    {
        var fromEnv = Environment.GetEnvironmentVariable("ADB");
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv)) return fromEnv;

        var fromSdk = AndroidSdk.CandidateRoots
            .Select(root => Path.Combine(root, "platform-tools", "adb.exe"))
            .FirstOrDefault(File.Exists);
        if (fromSdk != null) return fromSdk;

        var bundled = scrcpyPath == null ? null : Path.Combine(Path.GetDirectoryName(scrcpyPath)!, "adb.exe");
        return FindOnPath("adb.exe") ?? (File.Exists(bundled) ? bundled! : "adb");
    }

    // Reads the persisted user/machine PATH too, so a tool installed after the dashboard started (e.g. via winget) is found.
    private static string? FindOnPath(string fileName)
    {
        var dirs = new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine }
            .Select(target => Environment.GetEnvironmentVariable("PATH", target))
            .Where(path => !string.IsNullOrEmpty(path))
            .SelectMany(path => path!.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        foreach (var dir in dirs)
        {
            try
            {
                var candidate = Path.Combine(Environment.ExpandEnvironmentVariables(dir), fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // malformed PATH entry
            }
        }
        return null;
    }

    private static void AssignToKillOnCloseJob(ScrcpySession session)
    {
        if (KillOnCloseJob.Value == IntPtr.Zero) return;
        try
        {
            AssignProcessToJobObject(KillOnCloseJob.Value, session.ProcessHandle);
        }
        catch (InvalidOperationException)
        {
            // already exited
        }
    }

    private static IntPtr CreateKillOnCloseJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;

        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose | JobObjectLimitSilentBreakawayOk },
        };
        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, buffer, false);
            return SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, buffer, size) ? job : IntPtr.Zero;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitSilentBreakawayOk = 0x1000;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, int infoLength);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
