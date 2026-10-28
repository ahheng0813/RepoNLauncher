using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RepoNLauncher.Models;

namespace RepoNLauncher.Services;

/// <summary>
/// A Codex turn: codex exec --json, with the prompt on stdin. Codex doesn't stop to ask in exec mode; what it may do is
/// set by the sandbox (read-only, workspace-write or full access) chosen in the chat.
/// </summary>
public sealed class CodexChatTurn : AgentTurn
{
    private readonly ChatTurnRequest _request;
    private readonly HashSet<string> _started = new();

    public CodexChatTurn(string program, ChatTurnRequest request, Action<AgentEvent> onEvent) : base("Codex", onEvent)
    {
        _request = request;

        var args = new List<string> { "exec", "--json", "--skip-git-repo-check" };
        if (request.Model.Length > 0) args.AddRange(["-m", request.Model]);
        if (request.Effort.Length > 0) args.AddRange(["-c", $"model_reasoning_effort=\"{request.Effort}\""]);
        args.AddRange(["-c", $"sandbox_mode=\"{request.Mode}\""]);

        var images = request.Attachments.Where(a => a.IsImage).Select(a => $"--image={a.Path}");
        if (request.SessionId == null)
        {
            args.AddRange(["-C", request.WorkingDirectory]);
            args.AddRange(images);
            args.AddRange(["--", "-"]);
        }
        else
        {
            args.Add("resume");
            args.AddRange(images);
            args.AddRange(["--", request.SessionId, "-"]);
        }

        // Codex reads any file it's given a path to; non-image attachments are listed after the prompt.
        var prompt = request.Prompt;
        var files = request.Attachments.Where(a => !a.IsImage).Select(a => "- " + a.Path).ToList();
        if (files.Count > 0) prompt += $"\n\nAttached files:\n{string.Join("\n", files)}";

        if (Start(AgentCli.StartInfo(program, args, request.WorkingDirectory), prompt)) CloseInput();
    }

    protected override IEnumerable<AgentEvent> ParseLine(JsonElement line)
    {
        var events = new List<AgentEvent>();
        switch (CodexItems.Str(line, "type"))
        {
            case "thread.started" when CodexItems.Str(line, "thread_id") is { } id:
                events.Add(new SessionStartedEvent(id, _request.Model.Length > 0 ? _request.Model : null, _request.Mode));
                break;
            case "item.started" or "item.updated" or "item.completed" when line.TryGetProperty("item", out var item):
                CodexItems.Live(item, CodexItems.Str(line, "type") == "item.completed", _started, _request.WorkingDirectory, events);
                break;
            case "turn.completed":
                events.Add(new TurnFinishedEvent(false, null, Elapsed));
                break;
            case "turn.failed":
                var message = line.TryGetProperty("error", out var error) ? CodexItems.Str(error, "message") : null;
                events.Add(new TurnFinishedEvent(true, message ?? "The turn failed.", Elapsed));
                break;
            case "error" when CodexItems.Str(line, "message") is { } text:
                events.Add(new NoticeEvent(text, IsError: true));
                break;
        }
        return events;
    }
}

/// <summary>Codex's items (commands, file changes, messages), from codex exec --json or a saved session log.</summary>
public static partial class CodexItems
{
    /// <summary>One item of codex exec --json output; items arrive as started, updated and completed.</summary>
    public static void Live(JsonElement item, bool completed, HashSet<string> started, string? cwd, List<AgentEvent> events)
    {
        var id = Str(item, "id") ?? Guid.NewGuid().ToString();
        switch (Str(item, "type"))
        {
            case "agent_message" when completed && Str(item, "text") is { Length: > 0 } text:
                events.Add(new TextEvent(text));
                break;
            case "reasoning" when completed && Str(item, "text") is { Length: > 0 } text:
                events.Add(new ThinkingEvent(text));
                break;
            case "command_execution":
                StartOnce(events, started, id, Command(Str(item, "command") ?? string.Empty));
                if (completed)
                    events.Add(new ToolFinishedEvent(id, Str(item, "aggregated_output"),
                        Str(item, "status") == "failed" || (item.TryGetProperty("exit_code", out var code) && code.TryGetInt32(out var c) && c != 0)));
                break;
            case "file_change":
                StartOnce(events, started, id, FileChanges(item, cwd));
                if (completed) events.Add(new ToolFinishedEvent(id, null, Str(item, "status") == "failed"));
                break;
            case "mcp_tool_call":
                var arguments = item.TryGetProperty("arguments", out var a) && a.ValueKind != JsonValueKind.Null ? a.GetRawText() : null;
                StartOnce(events, started, id, new ToolDescription($"{Str(item, "server")} · {Str(item, "tool")}", ClaudeTools.FirstLine(arguments ?? string.Empty), arguments));
                if (completed)
                {
                    var failed = Str(item, "status") == "failed";
                    var output = failed && item.TryGetProperty("error", out var e) ? Str(e, "message") ?? e.GetRawText()
                        : item.TryGetProperty("result", out var r) && r.ValueKind != JsonValueKind.Null ? r.GetRawText() : null;
                    events.Add(new ToolFinishedEvent(id, output, failed));
                }
                break;
            case "web_search":
                StartOnce(events, started, id, new ToolDescription("Web search", Str(item, "query") ?? string.Empty));
                if (completed) events.Add(new ToolFinishedEvent(id, null, false));
                break;
            case "todo_list":
                // Re-sent on each update; the chat replaces the tool's description when the same id comes again.
                events.Add(new ToolStartedEvent(id, Todos(item)));
                started.Add(id);
                if (completed) events.Add(new ToolFinishedEvent(id, null, false));
                break;
            case "error" when Str(item, "message") is { } message:
                events.Add(new NoticeEvent(message, IsError: true));
                break;
        }
    }

    private static void StartOnce(List<AgentEvent> events, HashSet<string> started, string id, ToolDescription tool)
    {
        if (started.Add(id)) events.Add(new ToolStartedEvent(id, tool));
    }

    /// <summary>A shell command without the shell it's wrapped in: powershell.exe -Command '…' reads as just the script.</summary>
    public static ToolDescription Command(string command)
    {
        var match = WrappedCommand().Match(command);
        var script = match.Success ? match.Groups["script"].Value : command;
        if (script.Length >= 2 && (script[0] == '\'' || script[0] == '"') && script[^1] == script[0]) script = script[1..^1];
        return new ToolDescription("Shell", ClaudeTools.FirstLine(script), script);
    }

    private static ToolDescription FileChanges(JsonElement item, string? cwd)
    {
        var changes = item.TryGetProperty("changes", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(c => (Kind: Str(c, "kind") ?? "update", Path: PathText.Relative(Str(c, "path") ?? string.Empty, cwd))).ToList()
            : [];
        var summary = changes.Count == 1 ? changes[0].Path : $"{changes.Count} files";
        var detail = string.Join("\n", changes.Select(c => $"{(c.Kind switch { "add" => "+", "delete" => "-", _ => "~" })} {c.Path}"));
        return new ToolDescription("Edit", summary, detail.Length > 0 ? detail : null);
    }

    private static ToolDescription Todos(JsonElement item)
    {
        var todos = item.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().ToList() : [];
        var done = todos.Count(t => t.TryGetProperty("completed", out var c) && c.ValueKind == JsonValueKind.True);
        var lines = todos.Select(t => (t.TryGetProperty("completed", out var c) && c.ValueKind == JsonValueKind.True ? "☑ " : "☐ ") + Str(t, "text"));
        return new ToolDescription("Plan", $"{done} of {todos.Count} done", string.Join("\n", lines));
    }

    /// <summary>Events from a saved session log (~/.codex/sessions/…/rollout-*.jsonl).</summary>
    public static List<AgentEvent> ReadSaved(string path)
    {
        var lines = new List<JsonElement>();
        foreach (var raw in ReadLines(path))
        {
            using var document = Parse(raw);
            if (document != null) lines.Add(document.RootElement.Clone());
        }

        // Newer Codex logs every turn item as an item_completed event; older versions logged user_message, agent_message,
        // exec_command_end and so on. Read whichever this log has, never both.
        var itemEvents = lines.Any(l => Str(l, "type") == "event_msg" && Payload(l) is { } p && Str(p, "type") == "item_completed");
        var events = new List<AgentEvent>();
        string? cwd = null;
        foreach (var line in lines)
        {
            if (Payload(line) is not { } payload) continue;
            var type = Str(line, "type");
            if (type == "session_meta")
            {
                cwd = Str(payload, "cwd");
                if (Str(payload, "id") is { } id) events.Add(new SessionStartedEvent(id, null, null));
                continue;
            }
            if (type != "event_msg") continue;

            var kind = Str(payload, "type");
            if (itemEvents)
            {
                if (kind == "item_completed" && payload.TryGetProperty("item", out var item)) SavedItem(item, cwd, events);
                continue;
            }
            switch (kind)
            {
                case "user_message" when Str(payload, "message") is { } message:
                    events.Add(new UserMessageEvent(message.Trim(), []));
                    break;
                case "agent_message" when Str(payload, "message") is { Length: > 0 } message:
                    events.Add(new TextEvent(message));
                    break;
                case "agent_reasoning" when Str(payload, "text") is { Length: > 0 } text:
                    events.Add(new ThinkingEvent(text));
                    break;
                case "exec_command_end":
                    var callId = Str(payload, "call_id") ?? Guid.NewGuid().ToString();
                    events.Add(new ToolStartedEvent(callId, Command(JoinCommand(payload))));
                    events.Add(new ToolFinishedEvent(callId, Str(payload, "aggregated_output") ?? Str(payload, "stdout"),
                        payload.TryGetProperty("exit_code", out var code) && code.TryGetInt32(out var c) && c != 0));
                    break;
                case "patch_apply_end":
                    var patchId = Str(payload, "call_id") ?? Guid.NewGuid().ToString();
                    events.Add(new ToolStartedEvent(patchId, new ToolDescription("Edit", "Applied a patch", Str(payload, "stdout"))));
                    events.Add(new ToolFinishedEvent(patchId, null, payload.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False));
                    break;
            }
        }
        return events;
    }

    /// <summary>The working folder and first prompt of a saved session, read from the start of its log.</summary>
    public static ChatSessionSummary? ReadSummary(string path)
    {
        string? id = null, cwd = null;
        foreach (var raw in ReadLines(path))
        {
            // Skip the long instruction and tool lines without parsing them.
            if (!raw.Contains("\"session_meta\"") && !raw.Contains("\"user_message\"") && !raw.Contains("\"UserMessage\"")) continue;
            using var document = Parse(raw);
            if (document == null || Payload(document.RootElement) is not { } payload) continue;
            var line = document.RootElement;

            if (Str(line, "type") == "session_meta")
            {
                id = Str(payload, "id");
                cwd = Str(payload, "cwd");
                continue;
            }
            var prompt = Str(payload, "type") switch
            {
                "user_message" => Str(payload, "message"),
                "item_completed" when payload.TryGetProperty("item", out var item) && Str(item, "type") == "UserMessage" => ItemText(item),
                _ => null,
            };
            if (id != null && cwd != null && !string.IsNullOrWhiteSpace(prompt))
                return new ChatSessionSummary(AgentKind.Codex, id, Title(prompt), cwd, File.GetLastWriteTime(path), path);
        }
        return null;
    }

    public static string Title(string prompt)
    {
        var line = prompt.Trim().Split('\n')[0].Trim();
        return line.Length > 90 ? line[..90] + "…" : line;
    }

    private static void SavedItem(JsonElement item, string? cwd, List<AgentEvent> events)
    {
        var id = Str(item, "id") ?? Guid.NewGuid().ToString();
        switch (Str(item, "type"))
        {
            case "UserMessage" when ItemText(item) is { Length: > 0 } text:
                events.Add(new UserMessageEvent(text.Trim(), []));
                break;
            case "AgentMessage" when ItemText(item) is { Length: > 0 } text:
                events.Add(new TextEvent(text));
                break;
            case "Reasoning" when ItemText(item) is { Length: > 0 } text:
                events.Add(new ThinkingEvent(text));
                break;
            case "CommandExecution":
                events.Add(new ToolStartedEvent(id, Command(JoinCommand(item))));
                events.Add(new ToolFinishedEvent(id, Str(item, "aggregated_output"),
                    Str(item, "status") == "failed" || (item.TryGetProperty("exit_code", out var code) && code.TryGetInt32(out var c) && c != 0)));
                break;
            case "UserMessage" or "AgentMessage" or "Reasoning" or "ContextCompaction" or null:
                break;
            case var other:
                // File changes, MCP and web tools in the newer log: shown by type, with the item itself on expand.
                var title = Regex.Replace(other, "(?<=[a-z])(?=[A-Z])", " ");
                events.Add(new ToolStartedEvent(id, new ToolDescription(title, Str(item, "query") ?? Str(item, "tool") ?? string.Empty,
                    ClaudeTools.Clip(item.GetRawText(), 4000))));
                events.Add(new ToolFinishedEvent(id, null, Str(item, "status") == "failed"));
                break;
        }
    }

    // UserMessage/AgentMessage items keep their text as content parts ({type: text|Text, text}).
    private static string? ItemText(JsonElement item)
    {
        if (Str(item, "text") is { } direct) return direct;
        if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return null;
        var text = new StringBuilder();
        foreach (var part in content.EnumerateArray())
            if (Str(part, "text") is { } t) text.Append(t);
        return text.ToString();
    }

    // Logged commands are an argv array (["powershell.exe", "-Command", "…"]) or a string.
    private static string JoinCommand(JsonElement element)
    {
        if (!element.TryGetProperty("command", out var command)) return string.Empty;
        if (command.ValueKind == JsonValueKind.String) return command.GetString()!;
        if (command.ValueKind != JsonValueKind.Array) return string.Empty;
        var parts = command.EnumerateArray().Select(p => p.GetString() ?? string.Empty).ToList();
        var flag = parts.FindIndex(p => p is "-Command" or "-c" or "-lc" or "/c");
        return flag >= 0 && flag + 1 < parts.Count ? parts[flag + 1] : string.Join(" ", parts);
    }

    private static JsonDocument? Parse(string raw)
    {
        try { return JsonDocument.Parse(raw); }
        catch (JsonException) { return null; }
    }

    private static JsonElement? Payload(JsonElement line) =>
        line.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object ? payload : null;

    private static IEnumerable<string> ReadLines(string path)
    {
        FileStream stream;
        try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
            if (line.Length > 0) yield return line;
    }

    public static string? Str(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // "C:\…\powershell.exe" -Command '…', /bin/bash -lc '…', cmd.exe /c …
    [GeneratedRegex(@"^\s*""?[^""]*?(powershell|pwsh|bash|zsh|sh|cmd)(\.exe)?""?\s+(-NoProfile\s+)?(-Command|-lc|-c|/c)\s+(?<script>.*)$",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex WrappedCommand();
}
