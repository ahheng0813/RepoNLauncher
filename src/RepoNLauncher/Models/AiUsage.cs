namespace RepoNLauncher.Models;

/// <summary>Tokens used, split the way the providers count them.</summary>
public readonly record struct TokenCounts(long Input, long Output, long CacheWrite, long CacheRead)
{
    public long Total => Input + Output + CacheWrite + CacheRead;

    public static TokenCounts operator +(TokenCounts a, TokenCounts b) =>
        new(a.Input + b.Input, a.Output + b.Output, a.CacheWrite + b.CacheWrite, a.CacheRead + b.CacheRead);
}

/// <summary>One model response recorded in a session file on this PC.</summary>
public record UsageRecord(DateTimeOffset Time, string Model, TokenCounts Tokens);

/// <summary>One plan limit (the 5-hour session, the week…): how much of it is used and when it resets.</summary>
public record UsageLimit(string Label, double Percent, DateTimeOffset? ResetsAt);

/// <summary>Token use over one period, from the session files on this PC.</summary>
public record UsagePeriod(string Label, int Requests, TokenCounts Tokens);

public record ModelUsage(string Model, long Tokens);

/// <summary>What the dashboard knows about one AI coding tool: its sign-in, plan limits and recent use.</summary>
public record AiToolUsage
{
    public bool IsSignedIn { get; init; }
    public string? Account { get; init; }
    public string? Plan { get; init; }

    /// <summary>Plan limits as the tool last saved them; empty when it hasn't.</summary>
    public IReadOnlyList<UsageLimit> Limits { get; init; } = [];
    public DateTimeOffset? LimitsAsOf { get; init; }

    public bool HasLocalSessions { get; init; }
    public IReadOnlyList<UsagePeriod> Periods { get; init; } = [];

    /// <summary>Token share per model over the last 7 days, biggest first.</summary>
    public IReadOnlyList<ModelUsage> Models { get; init; } = [];
}
