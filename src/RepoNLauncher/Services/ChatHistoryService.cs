using System.IO;
using System.Text;
using System.Text.Json;
using RepoNLauncher.Models;

namespace RepoNLauncher.Services;

/// <summary>
/// Chats saved on this PC: Claude Code's transcripts (~/.claude/projects/&lt;folder&gt;/&lt;session&gt;.jsonl) and Codex's
/// session logs (~/.codex/sessions). Listing reads only the start and end of each file, and remembers what it read
/// until the file changes.
/// </summary>
public sealed class ChatHistoryService
{
    // Titles are appended as the chat goes on, so the latest is near the end; the first prompt is near the start.
    private const int HeadBytes = 256 * 1024;
    private const int TailBytes = 256 * 1024;

    private readonly object _lock = new();
    private readonly Dictionary<string, (long Length, DateTime Written, ChatSessionSummary? Summary)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ChatSessionSummary> List()
    {
        var files = ClaudeFiles().Select(f => (Agent: AgentKind.Claude, Path: f))
            .Concat(CodexFiles().Select(f => (Agent: AgentKind.Codex, Path: f)));

        var sessions = new List<ChatSessionSummary>();
        lock (_lock)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (agent, path) in files)
            {
                seen.Add(path);
                FileInfo info;
                try { info = new FileInfo(path); if (!info.Exists) continue; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

                if (!_cache.TryGetValue(path, out var cached) || cached.Length != info.Length || cached.Written != info.LastWriteTimeUtc)
                {
                    var summary = agent == AgentKind.Claude ? ReadClaudeSummary(path) : CodexItems.ReadSummary(path);
                    cached = (info.Length, info.LastWriteTimeUtc, summary);
                    _cache[path] = cached;
                }
                if (cached.Summary != null) sessions.Add(cached.Summary with { LastActive = info.LastWriteTime });
            }
            foreach (var gone in _cache.Keys.Where(k => !seen.Contains(k)).ToList()) _cache.Remove(gone);
        }

        // A resumed Codex session can appear in both the live and archived folders; keep the newest copy.
        return sessions
            .GroupBy(s => (s.Agent, s.Id))
            .Select(g => g.MaxBy(s => s.LastActive)!)
            .OrderByDescending(s => s.LastActive)
            .ToList();
    }

    /// <summary>Everything in a saved chat, as the events a live chat would have produced.</summary>
    public static IReadOnlyList<AgentEvent> Load(ChatSessionSummary session)
    {
        if (session.Agent == AgentKind.Codex) return CodexItems.ReadSaved(session.FilePath);

        var parser = new ClaudeEventParser(session.WorkingDirectory);
        var events = new List<AgentEvent>();
        var seen = new HashSet<string>();
        foreach (var raw in ReadLines(session.FilePath))
        {
            if (!raw.Contains("\"user\"") && !raw.Contains("\"assistant\"") && !raw.Contains("compact_boundary")) continue;
            using var document = Parse(raw);
            if (document == null) continue;
            var line = document.RootElement;
            // A resumed or rewound session can repeat entries; each has its own uuid.
            if (Str(line, "uuid") is { } uuid && !seen.Add(uuid)) continue;
            events.AddRange(parser.Parse(line));
        }
        return events;
    }

    private static ChatSessionSummary? ReadClaudeSummary(string path)
    {
        string? id = null, cwd = null, firstPrompt = null, customTitle = null, aiTitle = null;

        void Read(JsonElement line)
        {
            id ??= Str(line, "sessionId");
            cwd ??= Str(line, "cwd");
            switch (Str(line, "type"))
            {
                case "custom-title" when Str(line, "customTitle") is { Length: > 0 } title:
                    customTitle = title;
                    break;
                case "ai-title" when Str(line, "aiTitle") is { Length: > 0 } title:
                    aiTitle = title;
                    break;
                case "user" when firstPrompt == null && !Bool(line, "isMeta") && !Bool(line, "isSidechain") && !Bool(line, "isCompactSummary"):
                    firstPrompt = PromptText(line);
                    break;
            }
        }

        long length;
        try { length = new FileInfo(path).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }

        foreach (var raw in ReadChunk(path, 0, HeadBytes))
            using (var document = Parse(raw)) if (document != null) Read(document.RootElement);
        if (length > HeadBytes)
            foreach (var raw in ReadChunk(path, Math.Max(HeadBytes, length - TailBytes), TailBytes))
                using (var document = Parse(raw)) if (document != null) Read(document.RootElement);

        var title = customTitle ?? aiTitle ?? (firstPrompt != null ? CodexItems.Title(firstPrompt) : null);
        if (title == null || cwd == null) return null; // nothing said yet, or nowhere to resume it from
        return new ChatSessionSummary(AgentKind.Claude, id ?? Path.GetFileNameWithoutExtension(path), title, cwd, File.GetLastWriteTime(path), path);
    }

    private static string? PromptText(JsonElement line)
    {
        if (!line.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content)) return null;
        if (content.ValueKind == JsonValueKind.String) return ClaudeEventParser.UserText(content.GetString()!);
        if (content.ValueKind != JsonValueKind.Array) return null;
        foreach (var block in content.EnumerateArray())
            if (Str(block, "type") == "text" && Str(block, "text") is { } text && ClaudeEventParser.UserText(text) is { } prompt)
                return prompt;
        return null;
    }

    // Top level only: subagent transcripts live in subfolders.
    private static IEnumerable<string> ClaudeFiles()
    {
        var files = new List<string>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(AgentPaths.ClaudeProjectsDir))
                files.AddRange(Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.TopDirectoryOnly));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return files;
    }

    private static IEnumerable<string> CodexFiles()
    {
        var files = new List<string>();
        foreach (var dir in AgentPaths.CodexSessionDirs)
        {
            try { files.AddRange(Directory.EnumerateFiles(dir, "rollout-*.jsonl", SearchOption.AllDirectories)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return files;
    }

    // Whole lines from part of a file: a line cut off at either end of the chunk is dropped.
    private static List<string> ReadChunk(string path, long start, int count)
    {
        byte[] buffer;
        int read;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Position = start;
            buffer = new byte[count];
            read = stream.ReadAtLeast(buffer, count, throwOnEndOfStream: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }

        var lines = Encoding.UTF8.GetString(buffer, 0, read).Split('\n').ToList();
        if (start > 0 && lines.Count > 0) lines.RemoveAt(0);
        if (read == count && lines.Count > 0) lines.RemoveAt(lines.Count - 1);
        return lines.Where(l => l.Length > 0 && l[0] == '{').ToList();
    }

    private static IEnumerable<string> ReadLines(string path)
    {
        FileStream stream;
        try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
            if (line.Length > 0) yield return line;
    }

    private static JsonDocument? Parse(string raw)
    {
        try { return JsonDocument.Parse(raw); }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool Bool(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
}
