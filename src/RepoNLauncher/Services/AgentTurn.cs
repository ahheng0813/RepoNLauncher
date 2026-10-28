using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using RepoNLauncher.Models;

namespace RepoNLauncher.Services;

/// <summary>
/// One turn of a chat: the agent's program, run for one prompt, with its output turned into events. Each turn is a
/// fresh process that resumes the session, so model, effort and permissions can change between turns, as in the apps.
/// Events are raised on a background thread, in order, and always end with one <see cref="TurnFinishedEvent"/>.
/// </summary>
public abstract class AgentTurn
{
    private const int StderrLinesKept = 30;

    private readonly string _agentName;
    private readonly Action<AgentEvent> _onEvent;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Queue<string> _stderr = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _inputLock = new();
    private Process? _process;
    private Task _errorsRead = Task.CompletedTask;
    private bool _inputClosed;
    private bool _finished;
    private volatile bool _stopped;

    protected AgentTurn(string agentName, Action<AgentEvent> onEvent)
    {
        _agentName = agentName;
        _onEvent = onEvent;
    }

    public Task Completion => _completion.Task;

    protected TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>Answers a permission prompt. Only Claude Code asks; Codex runs within its sandbox instead.</summary>
    public virtual void Answer(string requestId, JsonObject response) { }

    public void Stop()
    {
        _stopped = true;
        try { _process?.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { }
    }

    protected bool Start(ProcessStartInfo info, string? input)
    {
        try
        {
            _process = Process.Start(info) ?? throw new InvalidOperationException("The process didn't start.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            Finish(new TurnFinishedEvent(true, $"Couldn't start {_agentName}: {ex.Message}", Elapsed));
            _completion.TrySetResult();
            return false;
        }

        _errorsRead = Task.Run(ReadErrorsAsync);
        _ = Task.Run(ReadOutputAsync);
        if (input != null) WriteLine(input);
        return true;
    }

    /// <summary>Events for one line of the program's output (a JSON object).</summary>
    protected abstract IEnumerable<AgentEvent> ParseLine(JsonElement line);

    protected void WriteLine(string line)
    {
        lock (_inputLock)
        {
            if (_inputClosed || _process == null) return;
            try
            {
                _process.StandardInput.WriteLine(line);
                _process.StandardInput.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        }
    }

    /// <summary>Ends the program's input, which tells it no more prompts or answers are coming.</summary>
    protected void CloseInput()
    {
        lock (_inputLock)
        {
            if (_inputClosed || _process == null) return;
            _inputClosed = true;
            try { _process.StandardInput.Close(); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        }
    }

    private async Task ReadOutputAsync()
    {
        var process = _process!;
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.Length == 0 || line[0] != '{') continue;
                List<AgentEvent> events;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    events = ParseLine(document.RootElement).ToList();
                }
                catch (JsonException)
                {
                    continue;
                }
                foreach (var e in events)
                {
                    if (e is TurnFinishedEvent finished) Finish(finished);
                    else if (!_finished) _onEvent(e);
                }
            }
            await process.WaitForExitAsync();
            await Task.WhenAny(_errorsRead, Task.Delay(500)); // the error message, if any, may still be arriving
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { }

        CloseInput();
        if (_stopped) Finish(new TurnFinishedEvent(false, "Stopped", Elapsed));
        else Finish(new TurnFinishedEvent(true, ExitMessage(process), Elapsed));
        _completion.TrySetResult();
    }

    // Only the first one counts: a turn that ended normally isn't reported again when the process exits.
    private void Finish(TurnFinishedEvent finished)
    {
        if (_finished) return;
        _finished = true;
        _onEvent(finished);
    }

    private string ExitMessage(Process process)
    {
        string[] errors;
        lock (_stderr) errors = _stderr.ToArray();
        var code = process.HasExited ? process.ExitCode.ToString() : "unknown";
        return errors.Length > 0
            ? $"{_agentName} stopped with an error:\n{string.Join("\n", errors)}"
            : $"{_agentName} stopped without finishing (exit code {code}).";
    }

    private async Task ReadErrorsAsync()
    {
        try
        {
            while (await _process!.StandardError.ReadLineAsync() is { } line)
            {
                if (line.Trim().Length == 0) continue;
                lock (_stderr)
                {
                    _stderr.Enqueue(line);
                    if (_stderr.Count > StderrLinesKept) _stderr.Dequeue();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { }
    }
}
