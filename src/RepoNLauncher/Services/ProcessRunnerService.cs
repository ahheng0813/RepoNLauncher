using System.Diagnostics;

namespace RepoNLauncher.Services;

/// <summary>Handle to a long-running, streamed process (e.g. `flutter run`, `dotnet run`).</summary>
public class RunningProcessHandle
{
    private readonly Process _process;

    public RunningProcessHandle(Process process) => _process = process;

    public bool HasExited
    {
        get
        {
            try { return _process.HasExited; }
            catch { return true; }
        }
    }

    public void Stop()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch { /* already exited */ }
    }
}

public record CaptureResult(int ExitCode, string StdOut, string StdErr);

public interface IProcessRunner
{
    /// <summary>Runs a short-lived shell command and waits for it to finish, capturing all output.</summary>
    Task<CaptureResult> RunCaptureAsync(string workingDirectory, string command, CancellationToken ct = default);

    /// <summary>Starts a long-running shell command, streaming each output line to the callback as it arrives.</summary>
    RunningProcessHandle StartStreaming(string workingDirectory, string command, Action<string> onOutputLine, Action<int> onExit);
}

/// <summary>
/// Runs everything through cmd.exe /c so that .bat/.cmd-based tools on the PATH
/// (flutter.bat, npm.cmd, etc.) launch the same way plain .exe tools (git, dotnet) do.
/// </summary>
public class ProcessRunnerService : IProcessRunner
{
    private static ProcessStartInfo BuildStartInfo(string workingDirectory, string command, bool redirectOutput)
    {
        return new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c {command}",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
    }

    public async Task<CaptureResult> RunCaptureAsync(string workingDirectory, string command, CancellationToken ct = default)
    {
        var startInfo = BuildStartInfo(workingDirectory, command, redirectOutput: true);
        // Nobody can answer a prompt in the hidden console, so git fails instead of waiting forever for a password.
        // Git Credential Manager still opens its own sign-in window.
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdOutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stdErrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);
        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;

        return new CaptureResult(process.ExitCode, stdOut, stdErr);
    }

    public RunningProcessHandle StartStreaming(string workingDirectory, string command, Action<string> onOutputLine, Action<int> onExit)
    {
        var startInfo = BuildStartInfo(workingDirectory, command, redirectOutput: true);
        // Python block-buffers output written to a pipe, so a running server's log would only show up in bursts.
        startInfo.Environment["PYTHONUNBUFFERED"] = "1";
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) onOutputLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) onOutputLine(e.Data);
        };
        process.Exited += (_, _) => onExit(process.ExitCode);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return new RunningProcessHandle(process);
    }
}
