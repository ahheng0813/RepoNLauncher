using System.IO;
using System.Text.Json.Nodes;

namespace RepoNLauncher.Models;

public enum AgentKind { Claude, Codex }

/// <summary>One choice in a model, effort or permission picker. An empty <see cref="Value"/> means the tool's own default.</summary>
public record AgentOption(string Value, string Label, string? Description = null)
{
    public override string ToString() => Label;
}

/// <summary>A file sent with a prompt. Images go to the model as images; anything else is passed by path.</summary>
public record ChatAttachment(string Path)
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp"];

    public string Name => System.IO.Path.GetFileName(Path);
    public bool IsImage => ImageExtensions.Contains(System.IO.Path.GetExtension(Path), StringComparer.OrdinalIgnoreCase);
}

/// <summary>What one turn of a chat sends. <see cref="SessionId"/> is null for a new chat.</summary>
public record ChatTurnRequest(
    string Prompt,
    IReadOnlyList<ChatAttachment> Attachments,
    string WorkingDirectory,
    string? SessionId,
    string Model,
    string Effort,
    string Mode);

/// <summary>A chat saved on this PC by Claude Code or Codex.</summary>
public record ChatSessionSummary(AgentKind Agent, string Id, string Title, string WorkingDirectory, DateTime LastActive, string FilePath);

/// <summary>A picture in a chat message: inline data from a transcript, or a file the user attached.</summary>
public record ChatImage(byte[]? Data, string? FilePath);

// What the agents report, turned into one vocabulary for the chat view, whether it streams in live or is read back from a saved chat.
public abstract record AgentEvent;

public sealed record SessionStartedEvent(string SessionId, string? Model, string? Mode) : AgentEvent;

public sealed record UserMessageEvent(string Text, IReadOnlyList<ChatImage> Images) : AgentEvent;

/// <summary>Part of a reply as it's written; the whole block follows as a <see cref="TextEvent"/>.</summary>
public sealed record TextDeltaEvent(string Text) : AgentEvent;

public sealed record TextEvent(string Text) : AgentEvent;

public sealed record ThinkingEvent(string Text) : AgentEvent;

public sealed record ToolStartedEvent(string Id, ToolDescription Tool) : AgentEvent;

public sealed record ToolFinishedEvent(string Id, string? Output, bool IsError) : AgentEvent;

/// <summary>Claude asks before running a tool. <see cref="Input"/> is the tool's input, which the answer may change.</summary>
public sealed record PermissionRequestEvent(string RequestId, string ToolUseId, string ToolName, JsonObject Input, JsonArray? Suggestions) : AgentEvent;

public sealed record NoticeEvent(string Text, bool IsError = false) : AgentEvent;

/// <summary>What the agent is busy with right now ("Writing note.txt"); null once it moves on.</summary>
public sealed record StatusEvent(string? Text) : AgentEvent;

public sealed record TurnFinishedEvent(bool IsError, string? Message, TimeSpan? Duration) : AgentEvent;

/// <summary>A tool call as shown in a chat: what it is, a one-line summary, and the full input when there's more to it.</summary>
public record ToolDescription(string Title, string Summary, string? Detail = null);

/// <summary>A reply to a <see cref="PermissionRequestEvent"/>, in the shape Claude Code reads.</summary>
public static class PermissionAnswer
{
    public static JsonObject Allow(JsonObject input, JsonArray? permissions = null)
    {
        var answer = new JsonObject { ["behavior"] = "allow", ["updatedInput"] = input.DeepClone() };
        if (permissions != null) answer["updatedPermissions"] = permissions.DeepClone();
        return answer;
    }

    public static JsonObject Deny(string message) => new() { ["behavior"] = "deny", ["message"] = message };
}

internal static class PathText
{
    /// <summary>A path relative to <paramref name="root"/> when it's inside it, otherwise as is.</summary>
    public static string Relative(string path, string? root)
    {
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(path)) return path;
        try
        {
            var relative = Path.GetRelativePath(root, path);
            return relative.StartsWith("..") || Path.IsPathRooted(relative) ? path : relative;
        }
        catch (ArgumentException)
        {
            return path;
        }
    }
}
