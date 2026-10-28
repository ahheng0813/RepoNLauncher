using System.Diagnostics;
using System.IO;
using System.Text;

namespace RepoNLauncher.Services;

/// <summary>Handle to a long-running, streamed process (e.g. `flutter run`, `dotnet run`).</summary>
public class RunningProcessHandle
{
    private readonly Process _process;
    private readonly Action _endPartialLines;

    public RunningProcessHandle(Process process, Action endPartialLines)
    {
        _process = process;
        _endPartialLines = endPartialLines;
    }

    public bool HasExited
    {
        get
        {
            try { return _process.HasExited; }
            catch { return true; }
        }
    }

    /// <summary>
    /// Types a line into the program's input and presses Enter, e.g. an answer to a prompt or flutter run's r.
    /// False when the program has stopped reading its input.
    /// </summary>
    public bool SendInput(string line)
    {
        try
        {
            // As in a terminal, where Enter starts a new line, output after this begins on a line of its own.
            _endPartialLines();
            _process.StandardInput.WriteLine(line);
            _process.StandardInput.Flush();
            return true;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return false;
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

    /// <summary>
    /// Starts a long-running shell command whose input <see cref="RunningProcessHandle.SendInput"/> writes to,
    /// streaming its output to the callback as it arrives. Each call gives a line and whether it's partial: the start
    /// of a line, such as a prompt waiting for an answer, that the next call replaces.
    /// </summary>
    RunningProcessHandle StartStreaming(string workingDirectory, string command, Action<string, bool> onOutput, Action<int> onExit);
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

    public RunningProcessHandle StartStreaming(string workingDirectory, string command, Action<string, bool> onOutput, Action<int> onExit)
    {
        var startInfo = BuildStartInfo(workingDirectory, command, redirectOutput: true);
        // Without this the program's input is the hidden console's, which nothing can type into: a prompt waits forever.
        startInfo.RedirectStandardInput = true;
        // Python block-buffers output written to a pipe, so a running server's log would only show up in bursts.
        startInfo.Environment["PYTHONUNBUFFERED"] = "1";
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        var reading = Task.CompletedTask;
        process.Exited += async (_, _) =>
        {
            // The last lines go before the exit line, without waiting on a child process that kept the output open.
            await Task.WhenAny(reading, Task.Delay(1000));
            onExit(process.ExitCode);
        };

        process.Start();
        var stdOut = new OutputReader(process.StandardOutput, onOutput);
        var stdErr = new OutputReader(process.StandardError, onOutput);
        reading = Task.WhenAll(stdOut.ReadAsync(), stdErr.ReadAsync());

        return new RunningProcessHandle(process, () =>
        {
            stdOut.EndLine();
            stdErr.EndLine();
        });
    }

    /// <summary>
    /// Reads an output stream as it comes rather than a line at a time, so a prompt that leaves the cursor on its line
    /// ("Name: ") shows up while it waits, as a partial line.
    /// </summary>
    private sealed class OutputReader(StreamReader reader, Action<string, bool> onOutput)
    {
        private readonly StringBuilder _pending = new();

        public async Task ReadAsync()
        {
            var buffer = new char[4096];
            try
            {
                int read;
                while ((read = await reader.ReadAsync(buffer)) > 0)
                {
                    var lines = new List<string>();
                    string partial;
                    // The callback waits on the UI thread, which may be in EndLine: don't hold the lock while calling it.
                    lock (_pending)
                    {
                        _pending.Append(buffer, 0, read);
                        partial = TakeLines(lines);
                    }
                    foreach (var line in lines) onOutput(line, false);
                    if (partial.Length > 0) onOutput(partial, true);
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { }

            // A last line without a newline.
            string rest;
            lock (_pending)
            {
                rest = _pending.ToString().TrimEnd('\r');
                _pending.Clear();
            }
            if (rest.Length > 0) onOutput(rest, false);
        }

        /// <summary>Input was sent: the line so far is done, and is already on screen as a partial one.</summary>
        public void EndLine()
        {
            lock (_pending) _pending.Clear();
        }

        // Moves the complete lines into lines, ending at \n, \r\n or a lone \r as StreamReader.ReadLine does,
        // and returns the start of the next one. A \r at the very end waits in case a \n follows.
        private string TakeLines(List<string> lines)
        {
            var text = _pending.ToString();
            var start = 0;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n' || (text[i] == '\r' && i + 1 < text.Length))
                {
                    lines.Add(text[start..i]);
                    if (text[i] == '\r' && text[i + 1] == '\n') i++;
                    start = i + 1;
                }
            }
            _pending.Clear().Append(text, start, text.Length - start);
            return text[start..].TrimEnd('\r');
        }
    }
}
