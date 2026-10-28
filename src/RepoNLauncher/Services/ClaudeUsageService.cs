using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RepoNLauncher.Models;

namespace RepoNLauncher.Services;

/// <summary>
/// Claude Code usage, read from what Claude Code itself stores on this PC: the account and the plan limits it last
/// fetched (~/.claude.json), and the token counts in its session transcripts (~/.claude/projects). Claude Code's
/// sign-in is never read or used.
/// </summary>
public class ClaudeUsageService
{
    // Plan limits Claude Code saves, in the order shown. Others in that cache are internal experiments.
    private static readonly (string Key, string Label)[] KnownLimits =
    {
        ("five_hour", "Session (5 hours)"),
        ("seven_day", "Week (all models)"),
        ("seven_day_opus", "Week (Opus)"),
        ("seven_day_sonnet", "Week (Sonnet)"),
    };

    private static readonly byte[] UsageMarker = "\"usage\""u8.ToArray();
    private static readonly byte[] AssistantMarker = "\"assistant\""u8.ToArray();

    private readonly JsonlTail<ClaudeResponse> _transcripts = new(ParseLine);

    public string UsagePageUrl => "https://claude.ai/settings/usage";

    public AiToolUsage Read()
    {
        var usage = ReadState(AgentPaths.ClaudeStatePath);
        var records = ReadTranscripts(AgentPaths.ClaudeProjectsDir);
        var (periods, models) = UsageSummary.Summarize(records);
        return usage with { HasLocalSessions = records.Count > 0, Periods = periods, Models = models };
    }

    private static AiToolUsage ReadState(string statePath)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(statePath));
            root = document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AiToolUsage();
        }

        if (!root.TryGetProperty("oauthAccount", out var account) || account.ValueKind != JsonValueKind.Object)
            return new AiToolUsage();

        var name = String(account, "displayName");
        var email = String(account, "emailAddress");
        var usage = new AiToolUsage
        {
            IsSignedIn = true,
            Account = name != null && email != null ? $"{name} ({email})" : name ?? email,
            Plan = PlanName(String(account, "organizationType")),
        };

        // The cache belongs to whichever account fetched it; after switching accounts it would be someone else's.
        if (!root.TryGetProperty("cachedUsageUtilization", out var cache) || cache.ValueKind != JsonValueKind.Object ||
            String(cache, "accountUuid") != String(account, "accountUuid") ||
            !cache.TryGetProperty("utilization", out var utilization) || utilization.ValueKind != JsonValueKind.Object)
            return usage;

        var limits = new List<UsageLimit>();
        foreach (var (key, label) in KnownLimits)
        {
            if (!utilization.TryGetProperty(key, out var limit) || limit.ValueKind != JsonValueKind.Object) continue;
            if (!limit.TryGetProperty("utilization", out var percent) || percent.ValueKind != JsonValueKind.Number) continue;
            var resetsAt = DateTimeOffset.TryParse(String(limit, "resets_at"), out var reset) ? reset : (DateTimeOffset?)null;
            limits.Add(new UsageLimit(label, percent.GetDouble(), resetsAt));
        }

        var asOf = cache.TryGetProperty("fetchedAtMs", out var fetched) && fetched.TryGetInt64(out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : (DateTimeOffset?)null;
        return usage with { Limits = limits, LimitsAsOf = asOf };
    }

    private List<UsageRecord> ReadTranscripts(string projectsDir)
    {
        var since = DateTime.Now - UsageSummary.Window;
        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(projectsDir, "*.jsonl", SearchOption.AllDirectories)
                .Where(path => File.GetLastWriteTime(path) >= since)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { files = []; }

        // A response is written once per content block, and resumed sessions copy earlier ones, so count each message once.
        var seen = new HashSet<string>();
        var cutoff = DateTimeOffset.Now - UsageSummary.Window;
        return _transcripts.Read(files).Values
            .SelectMany(responses => responses)
            .Where(r => r.Time >= cutoff && seen.Add(r.MessageId))
            .Select(r => new UsageRecord(r.Time, r.Model, r.Tokens))
            .ToList();
    }

    private static ClaudeResponse? ParseLine(ReadOnlySpan<byte> line)
    {
        // Most lines are prompts, tool results and file snapshots; skip them before parsing.
        if (line.IndexOf(UsageMarker) < 0 || line.IndexOf(AssistantMarker) < 0) return null;
        try
        {
            var entry = JsonSerializer.Deserialize<TranscriptLine>(line);
            if (entry is not { Type: "assistant", Message: { Id: { } id, Model: { } model, Usage: { } usage } }) return null;
            if (model.StartsWith('<')) return null; // "<synthetic>": messages Claude Code makes up locally, no model call
            return new ClaudeResponse(id, entry.Timestamp, model,
                new TokenCounts(usage.Input, usage.Output, usage.CacheWrite, usage.CacheRead));
        }
        catch (JsonException) { return null; }
    }

    // "claude_pro" -> "Pro", "claude_max" -> "Max"
    private static string? PlanName(string? organizationType)
    {
        if (string.IsNullOrEmpty(organizationType)) return null;
        var plan = organizationType.StartsWith("claude_") ? organizationType["claude_".Length..] : organizationType;
        return plan.Length == 0 ? null : char.ToUpperInvariant(plan[0]) + plan[1..];
    }

    private static string? String(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed record ClaudeResponse(string MessageId, DateTimeOffset Time, string Model, TokenCounts Tokens);

    private sealed class TranscriptLine
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("timestamp")] public DateTimeOffset Timestamp { get; set; }
        [JsonPropertyName("message")] public TranscriptMessage? Message { get; set; }
    }

    private sealed class TranscriptMessage
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("model")] public string? Model { get; set; }
        [JsonPropertyName("usage")] public TranscriptUsage? Usage { get; set; }
    }

    private sealed class TranscriptUsage
    {
        [JsonPropertyName("input_tokens")] public long Input { get; set; }
        [JsonPropertyName("output_tokens")] public long Output { get; set; }
        [JsonPropertyName("cache_creation_input_tokens")] public long CacheWrite { get; set; }
        [JsonPropertyName("cache_read_input_tokens")] public long CacheRead { get; set; }
    }
}
