using RepoNLauncher.Models;

namespace RepoNLauncher.Services;

/// <summary>Totals model responses into today / last 7 days / last 30 days, and per model over the last 7 days.</summary>
public static class UsageSummary
{
    /// <summary>How far back session files are read; the longest period shown.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(30);

    public static (IReadOnlyList<UsagePeriod> Periods, IReadOnlyList<ModelUsage> Models) Summarize(IReadOnlyCollection<UsageRecord> records)
    {
        var now = DateTimeOffset.Now;
        var periods = new List<UsagePeriod>
        {
            Period("Today", records, new DateTimeOffset(now.Date, now.Offset)),
            Period("Last 7 days", records, now.AddDays(-7)),
            Period("Last 30 days", records, now - Window),
        };

        var weekStart = now.AddDays(-7);
        var models = records
            .Where(r => r.Time >= weekStart)
            .GroupBy(r => r.Model)
            .Select(g => new ModelUsage(g.Key, g.Sum(r => r.Tokens.Total)))
            .Where(m => m.Tokens > 0)
            .OrderByDescending(m => m.Tokens)
            .ToList();

        return (periods, models);
    }

    private static UsagePeriod Period(string label, IEnumerable<UsageRecord> records, DateTimeOffset since)
    {
        var inPeriod = records.Where(r => r.Time >= since).ToList();
        return new UsagePeriod(label, inPeriod.Count, inPeriod.Aggregate(default(TokenCounts), (sum, r) => sum + r.Tokens));
    }
}
