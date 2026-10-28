using System.IO;
using System.Text.Json;
using RepoNLauncher.Models;

namespace RepoNLauncher.Services;

/// <summary>
/// Codex usage, read from what Codex stores on this PC: whether it's signed in (auth.json, of which only the sign-in
/// method is read) and the session logs Codex CLI and the IDE extension write (~/.codex/sessions). Their token_count
/// entries carry the tokens used and the plan limits as of that moment. The Codex app keeps its sessions in the
/// cloud, so its use only shows on the usage page.
/// </summary>
public class CodexUsageService
{
    private static readonly byte[] TokenCountMarker = "\"token_count\""u8.ToArray();
    private static readonly byte[] TurnContextMarker = "\"turn_context\""u8.ToArray();

    private readonly JsonlTail<CodexLine> _sessions = new(ParseLine);

    public string UsagePageUrl => "https://chatgpt.com/codex/settings/usage";

    public AiToolUsage Read()
    {
        var usage = ReadSignIn(Path.Combine(AgentPaths.CodexHome, "auth.json"));
        var records = new List<UsageRecord>();
        CodexLine? latestLimits = null;
        var cutoff = DateTimeOffset.Now - UsageSummary.Window;

        foreach (var lines in _sessions.Read(SessionFiles()).Values)
        {
            // Each token_count carries the session's running total; the difference from the previous one is what that
            // turn used (Codex sometimes repeats an entry, which then adds nothing).
            var model = "unknown model";
            TokenCounts? previous = null;
            foreach (var line in lines)
            {
                if (line.Model != null) model = line.Model;
                if (line.Total is { } total)
                {
                    var used = previous is { } before && total.Total >= before.Total ? Minus(total, before) : total;
                    previous = total;
                    if (used.Total > 0 && line.Time >= cutoff) records.Add(new UsageRecord(line.Time, model, used));
                }
                if (line.Limits.Count > 0 && (latestLimits == null || line.Time > latestLimits.Time)) latestLimits = line;
            }
        }

        var (periods, models) = UsageSummary.Summarize(records);
        return usage with
        {
            Plan = latestLimits?.Plan ?? usage.Plan,
            Limits = latestLimits?.Limits ?? [],
            LimitsAsOf = latestLimits?.Time,
            HasLocalSessions = records.Count > 0,
            Periods = periods,
            Models = models,
        };
    }

    private static AiToolUsage ReadSignIn(string authPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(authPath));
            var root = document.RootElement;
            var mode = root.TryGetProperty("auth_mode", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var hasApiKey = root.TryGetProperty("OPENAI_API_KEY", out var key) && key.ValueKind == JsonValueKind.String;
            return mode switch
            {
                "chatgpt" => new AiToolUsage { IsSignedIn = true, Account = "ChatGPT account" },
                _ when mode == "apikey" || hasApiKey => new AiToolUsage { IsSignedIn = true, Account = "OpenAI API key" },
                _ => new AiToolUsage(),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AiToolUsage();
        }
    }

    // sessions/YYYY/MM/DD/rollout-*.jsonl, plus archived_sessions; only those written to within the window.
    private static List<string> SessionFiles()
    {
        var since = DateTime.Now - UsageSummary.Window;
        var files = new List<string>();
        foreach (var dir in AgentPaths.CodexSessionDirs)
        {
            try
            {
                files.AddRange(Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.AllDirectories)
                    .Where(path => File.GetLastWriteTime(path) >= since));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return files;
    }

    private static CodexLine? ParseLine(ReadOnlySpan<byte> line)
    {
        var isTurnContext = line.IndexOf(TurnContextMarker) >= 0;
        if (!isTurnContext && line.IndexOf(TokenCountMarker) < 0) return null;
        try
        {
            using var document = JsonDocument.Parse(line.ToArray());
            var root = document.RootElement;
            if (!DateTimeOffset.TryParse(String(root, "timestamp"), out var time) ||
                !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                return null;

            if (String(root, "type") == "turn_context")
                return String(payload, "model") is { } model ? new CodexLine(time, model, null, [], null) : null;

            if (String(payload, "type") != "token_count") return null;

            TokenCounts? total = null;
            if (payload.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object &&
                info.TryGetProperty("total_token_usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                // input_tokens includes the cached part.
                var cached = Long(usage, "cached_input_tokens");
                total = new TokenCounts(Long(usage, "input_tokens") - cached, Long(usage, "output_tokens"), 0, cached);
            }

            var limits = new List<UsageLimit>();
            string? plan = null;
            if (payload.TryGetProperty("rate_limits", out var rateLimits) && rateLimits.ValueKind == JsonValueKind.Object)
            {
                AddLimit(limits, rateLimits, "primary", "Session (5 hours)", time);
                AddLimit(limits, rateLimits, "secondary", "Week", time);
                plan = String(rateLimits, "plan_type") is { Length: > 0 } p ? char.ToUpperInvariant(p[0]) + p[1..] : null;
            }

            return total == null && limits.Count == 0 ? null : new CodexLine(time, null, total, limits, plan);
        }
        catch (JsonException) { return null; }
    }

    private static void AddLimit(List<UsageLimit> limits, JsonElement rateLimits, string window, string fallbackLabel, DateTimeOffset time)
    {
        if (!rateLimits.TryGetProperty(window, out var limit) || limit.ValueKind != JsonValueKind.Object) return;
        if (!limit.TryGetProperty("used_percent", out var used) || used.ValueKind != JsonValueKind.Number) return;

        // Newer Codex writes when the window resets; older versions wrote how long until it did.
        DateTimeOffset? resetsAt = null;
        if (limit.TryGetProperty("resets_at", out var at) && at.TryGetInt64(out var unix)) resetsAt = DateTimeOffset.FromUnixTimeSeconds(unix);
        else if (limit.TryGetProperty("resets_in_seconds", out var inSeconds) && inSeconds.TryGetInt64(out var seconds)) resetsAt = time.AddSeconds(seconds);

        var minutes = limit.TryGetProperty("window_minutes", out var length) && length.TryGetInt64(out var m) ? m : 0;
        var label = minutes switch
        {
            300 => "Session (5 hours)",
            10080 => "Week",
            > 0 and < 1440 => $"{minutes / 60} hours",
            >= 1440 => $"{minutes / 1440} days",
            _ => fallbackLabel,
        };
        limits.Add(new UsageLimit(label, used.GetDouble(), resetsAt));
    }

    private static TokenCounts Minus(TokenCounts a, TokenCounts b) =>
        new(a.Input - b.Input, a.Output - b.Output, a.CacheWrite - b.CacheWrite, a.CacheRead - b.CacheRead);

    private static long Long(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt64(out var number) ? number : 0;

    private static string? String(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>The model a session switched to, or a token_count entry (running total and/or plan limits).</summary>
    private sealed record CodexLine(DateTimeOffset Time, string? Model, TokenCounts? Total, IReadOnlyList<UsageLimit> Limits, string? Plan);
}
