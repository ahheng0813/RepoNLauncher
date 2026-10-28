using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RepoNLauncher.Models;

namespace RepoNLauncher.Services;

/// <summary>
/// Turns Claude Code's JSON lines into chat events. The live output of claude -p (stream-json) and a saved transcript
/// (~/.claude/projects/*/*.jsonl) share one message shape, so both go through here; only the live stream has partial
/// text and permission prompts, and only a transcript has the user's own messages.
/// </summary>
public sealed partial class ClaudeEventParser(string? workingDirectory)
{
    public const string CompactedNotice = "Earlier messages were summarized to free up context.";

    public IEnumerable<AgentEvent> Parse(JsonElement line)
    {
        var events = new List<AgentEvent>();
        switch (Str(line, "type"))
        {
            case "system":
                if (Str(line, "subtype") == "init")
                {
                    workingDirectory = Str(line, "cwd") ?? workingDirectory;
                    if (Str(line, "session_id") is { } id) events.Add(new SessionStartedEvent(id, Str(line, "model"), Str(line, "permissionMode")));
                }
                else if (Str(line, "subtype") == "compact_boundary")
                {
                    events.Add(new NoticeEvent(CompactedNotice));
                }
                else if (Str(line, "subtype") == "task_summary")
                {
                    events.Add(new StatusEvent(Str(line, "detail")));
                }
                break;

            case "stream_event" when !IsSubagent(line):
                if (line.TryGetProperty("event", out var ev) && Str(ev, "type") == "content_block_delta" &&
                    ev.TryGetProperty("delta", out var delta) && Str(delta, "type") == "text_delta" && Str(delta, "text") is { } part)
                    events.Add(new TextDeltaEvent(part));
                break;

            case "assistant" when !IsSubagent(line):
                foreach (var block in Blocks(line))
                {
                    switch (Str(block, "type"))
                    {
                        case "text" when Str(block, "text") is { Length: > 0 } text:
                            events.Add(new TextEvent(text));
                            break;
                        case "thinking" when Str(block, "thinking") is { Length: > 0 } thinking:
                            events.Add(new ThinkingEvent(thinking));
                            break;
                        case "tool_use" or "server_tool_use" when Str(block, "id") is { } id:
                            var input = block.TryGetProperty("input", out var i) ? i : default;
                            events.Add(new ToolStartedEvent(id, ClaudeTools.Describe(Str(block, "name") ?? "Tool", input, workingDirectory)));
                            break;
                        case "web_search_tool_result" when Str(block, "tool_use_id") is { } id:
                            events.Add(new ToolFinishedEvent(id, null, false));
                            break;
                    }
                }
                break;

            case "user" when !IsSubagent(line) && !Bool(line, "isMeta"):
                if (Bool(line, "isCompactSummary"))
                {
                    events.Add(new NoticeEvent(CompactedNotice));
                    break;
                }
                var texts = new List<string>();
                var images = new List<ChatImage>();
                if (line.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content))
                {
                    if (content.ValueKind == JsonValueKind.String)
                    {
                        texts.Add(content.GetString()!);
                    }
                    else if (content.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var block in content.EnumerateArray())
                        {
                            switch (Str(block, "type"))
                            {
                                case "text" when Str(block, "text") is { } typed:
                                    texts.Add(typed);
                                    break;
                                case "image" when Image(block) is { } image:
                                    images.Add(image);
                                    break;
                                case "tool_result" when Str(block, "tool_use_id") is { } id:
                                    events.Add(new ToolFinishedEvent(id, ResultText(block), Bool(block, "is_error")));
                                    break;
                            }
                        }
                    }
                }
                AddUserText(events, texts, images);
                break;

            case "control_request":
                if (line.TryGetProperty("request", out var request) && Str(request, "subtype") == "can_use_tool" &&
                    Str(line, "request_id") is { } requestId)
                {
                    var input = request.TryGetProperty("input", out var i) && i.ValueKind == JsonValueKind.Object
                        ? JsonNode.Parse(i.GetRawText())!.AsObject()
                        : new JsonObject();
                    var suggestions = request.TryGetProperty("permission_suggestions", out var s) && s.ValueKind == JsonValueKind.Array
                        ? JsonNode.Parse(s.GetRawText())!.AsArray()
                        : null;
                    events.Add(new PermissionRequestEvent(requestId, Str(request, "tool_use_id") ?? requestId,
                        Str(request, "tool_name") ?? "Tool", input, suggestions is { Count: > 0 } ? suggestions : null));
                }
                break;

            case "result":
                var isError = Bool(line, "is_error");
                var duration = line.TryGetProperty("duration_ms", out var ms) && ms.TryGetDouble(out var d) ? TimeSpan.FromMilliseconds(d) : (TimeSpan?)null;
                string? error = null;
                if (isError)
                {
                    error = Str(line, "result");
                    if (string.IsNullOrWhiteSpace(error) && line.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                        error = string.Join("\n", errors.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText()));
                    if (string.IsNullOrWhiteSpace(error)) error = Str(line, "subtype");
                }
                events.Add(new TurnFinishedEvent(isError, error, duration));
                break;
        }
        return events;
    }

    /// <summary>The text a user typed, without the tags Claude Code wraps around commands and context it adds. Null if nothing's left.</summary>
    public static string? UserText(string raw)
    {
        if (raw.StartsWith("<local-command") || raw.StartsWith("<bash-stdout") || raw.StartsWith("<bash-stderr")) return null;
        if (CommandName().Match(raw) is { Success: true } command)
        {
            var args = CommandArgs().Match(raw);
            return args.Success && args.Groups[1].Value.Trim().Length > 0 ? $"{command.Groups[1].Value} {args.Groups[1].Value.Trim()}" : command.Groups[1].Value;
        }
        var text = AddedContext().Replace(raw, string.Empty).Trim();
        return text.Length == 0 ? null : text;
    }

    private static void AddUserText(List<AgentEvent> events, List<string> texts, List<ChatImage> images)
    {
        var kept = new List<string>();
        foreach (var raw in texts)
        {
            if (raw.StartsWith("[Request interrupted by user"))
            {
                events.Add(new NoticeEvent("Interrupted"));
                continue;
            }
            if (UserText(raw) is { } text) kept.Add(text);
        }
        if (kept.Count > 0 || images.Count > 0) events.Add(new UserMessageEvent(string.Join("\n\n", kept), images));
    }

    // Subagent work (a Task tool's own conversation) isn't part of the main chat.
    private static bool IsSubagent(JsonElement line) =>
        Bool(line, "isSidechain") || (line.TryGetProperty("parent_tool_use_id", out var parent) && parent.ValueKind == JsonValueKind.String);

    private static IEnumerable<JsonElement> Blocks(JsonElement line) =>
        line.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
            ? content.EnumerateArray()
            : [];

    private static ChatImage? Image(JsonElement block)
    {
        if (!block.TryGetProperty("source", out var source) || Str(source, "type") != "base64" || Str(source, "data") is not { } data) return null;
        try { return new ChatImage(Convert.FromBase64String(data), null); }
        catch (FormatException) { return null; }
    }

    private static string? ResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var content)) return null;
        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind != JsonValueKind.Array) return null;

        var text = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (text.Length > 0) text.Append('\n');
            text.Append(Str(part, "type") == "text" ? Str(part, "text") : $"[{Str(part, "type")}]");
        }
        return text.ToString();
    }

    private static string? Str(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool Bool(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    [GeneratedRegex(@"<command-name>\s*(.*?)\s*</command-name>", RegexOptions.Singleline)]
    private static partial Regex CommandName();

    [GeneratedRegex(@"<command-args>(.*?)</command-args>", RegexOptions.Singleline)]
    private static partial Regex CommandArgs();

    // Reminders and editor context Claude Code adds to a prompt.
    [GeneratedRegex(@"<(system-reminder|ide_[a-z_]+|user-prompt-submit-hook)>.*?</\1>", RegexOptions.Singleline)]
    private static partial Regex AddedContext();
}

/// <summary>How each Claude Code tool call reads in the chat.</summary>
public static class ClaudeTools
{
    public static ToolDescription Describe(string name, JsonElement input, string? cwd)
    {
        string? S(string property) =>
            input.ValueKind == JsonValueKind.Object && input.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        string P(string property) => PathText.Relative(S(property) ?? string.Empty, cwd);

        switch (name)
        {
            case "Bash" or "PowerShell":
                var command = S("command") ?? string.Empty;
                return new(name, S("description") ?? FirstLine(command), command);
            case "Read":
                return new("Read", P("file_path"));
            case "Write":
                return new("Write", P("file_path"), Clip(S("content")));
            case "Edit":
                return new("Edit", P("file_path"), Diff(S("old_string"), S("new_string")));
            case "MultiEdit":
                var edits = input.TryGetProperty("edits", out var list) && list.ValueKind == JsonValueKind.Array
                    ? string.Join("\n\n", list.EnumerateArray().Select(e => Diff(Prop(e, "old_string"), Prop(e, "new_string"))))
                    : null;
                return new("Edit", P("file_path"), edits);
            case "NotebookEdit":
                return new("Edit notebook", P("notebook_path"), Clip(S("new_source")));
            case "Glob" or "Grep":
                return new(name, S("pattern") + (S("path") is { } path ? $"  in {PathText.Relative(path, cwd)}" : string.Empty));
            case "WebFetch":
                return new("Fetch", S("url") ?? string.Empty, S("prompt"));
            case "WebSearch" or "web_search":
                return new("Web search", S("query") ?? string.Empty);
            case "Task" or "Agent":
                return new("Agent", S("description") ?? S("subagent_type") ?? string.Empty, S("prompt"));
            case "TodoWrite":
                return Todos(input);
            case "Skill":
                return new("Skill", S("skill") ?? S("command") ?? string.Empty, S("args"));
            case "ExitPlanMode":
                return new("Plan", "Ready to start", S("plan"));
            case "AskUserQuestion":
                return new("Question", FirstQuestion(input) ?? "Asked you a question");
            case "ToolSearch":
                return new("Tool search", S("query") ?? string.Empty);
        }

        // mcp__server__tool, and anything newer than this list: show the first text input, and all of it on expand.
        var title = name.StartsWith("mcp__") && name.Split("__") is { Length: >= 3 } parts ? $"{parts[1]} · {parts[2]}" : name;
        var summary = input.ValueKind == JsonValueKind.Object
            ? input.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).Select(p => FirstLine(p.Value.GetString()!)).FirstOrDefault()
            : null;
        var detail = input.ValueKind == JsonValueKind.Object && input.EnumerateObject().Any()
            ? Clip(JsonSerializer.Serialize(input, new JsonSerializerOptions { WriteIndented = true }))
            : null;
        return new(title, summary ?? string.Empty, detail);
    }

    private static ToolDescription Todos(JsonElement input)
    {
        if (!input.TryGetProperty("todos", out var todos) || todos.ValueKind != JsonValueKind.Array) return new("Update todos", string.Empty);
        var items = todos.EnumerateArray().ToList();
        var done = items.Count(t => Prop(t, "status") == "completed");
        var lines = items.Select(t => (Prop(t, "status") switch { "completed" => "☑ ", "in_progress" => "▸ ", _ => "☐ " }) + Prop(t, "content"));
        return new("Update todos", $"{done} of {items.Count} done", string.Join("\n", lines));
    }

    private static string? FirstQuestion(JsonElement input) =>
        input.TryGetProperty("questions", out var questions) && questions.ValueKind == JsonValueKind.Array && questions.GetArrayLength() > 0
            ? Prop(questions[0], "question")
            : null;

    private static string? Prop(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Diff(string? oldText, string? newText)
    {
        if (oldText == null && newText == null) return null;
        var lines = (oldText ?? string.Empty).Split('\n').Select(l => "- " + l.TrimEnd('\r'))
            .Concat((newText ?? string.Empty).Split('\n').Select(l => "+ " + l.TrimEnd('\r')));
        return Clip(string.Join("\n", lines));
    }

    public static string FirstLine(string text)
    {
        var line = text.TrimStart().Split('\n')[0].TrimEnd('\r');
        return line.Length > 160 ? line[..160] + "…" : line;
    }

    /// <summary>Long tool input or output, cut to what's worth showing in a chat.</summary>
    public static string? Clip(string? text, int max = 20_000) =>
        text == null || text.Length <= max ? text : text[..max] + $"\n… ({text.Length - max:N0} more characters)";
}
