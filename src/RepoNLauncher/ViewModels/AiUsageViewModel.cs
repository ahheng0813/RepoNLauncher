using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoNLauncher.Models;
using RepoNLauncher.Services;

namespace RepoNLauncher.ViewModels;

/// <summary>The AI usage window: Claude Code and Codex sign-in, plan limits and token use, read from their files on this PC.</summary>
public partial class AiUsageViewModel : ObservableObject
{
    private readonly ClaudeUsageService _claude;
    private readonly CodexUsageService _codex;
    private DateTime _limitsCheckedAt = DateTime.MinValue;

    public AiUsageViewModel(ClaudeUsageService claude, CodexUsageService codex)
    {
        _claude = claude;
        _codex = codex;
    }

    [ObservableProperty] private AiToolCardViewModel? _claudeCard;
    [ObservableProperty] private AiToolCardViewModel? _codexCard;
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private string _status = "Reading usage…";

    [RelayCommand]
    private Task Refresh() => RefreshAsync(checkLimits: true);

    /// <summary>
    /// Reads the usage files again. Every few minutes, or when <paramref name="checkLimits"/>, Claude Code and Codex are
    /// also asked for the plan limits: they only save new ones while in use, and plans are shared with their websites and apps.
    /// </summary>
    public async Task RefreshAsync(bool checkLimits = false)
    {
        // The readers keep what they've parsed between calls and aren't thread-safe, so one refresh at a time.
        if (IsRefreshing) return;
        IsRefreshing = true;
        try
        {
            checkLimits |= DateTime.Now - _limitsCheckedAt >= AiToolCardViewModel.CheckLimitsEvery;
            // Asking takes a few seconds; the first time, show what's saved meanwhile.
            if (checkLimits && ClaudeCard == null) await ReadAsync(checkLimits: false);
            if (checkLimits) Status = "Checking plan limits with Claude Code and Codex…";
            await ReadAsync(checkLimits);
            if (checkLimits) _limitsCheckedAt = DateTime.Now;
            Status = $"Updated {DateTime.Now:t}. Refreshes every minute.";
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private async Task ReadAsync(bool checkLimits)
    {
        var (claudeCli, codexCli) = await Task.Run(() => (AgentCli.FindClaude(), AgentCli.FindCodex()));
        var claude = Task.Run(() => _claude.Read(checkLimits ? claudeCli : null));
        var codex = Task.Run(() => _codex.Read(checkLimits ? codexCli : null));
        ClaudeCard = AiToolCardViewModel.ForClaude(await claude, _claude.UsagePageUrl, claudeCli);
        CodexCard = AiToolCardViewModel.ForCodex(await codex, _codex.UsagePageUrl, codexCli);
    }
}

/// <summary>One tool's card, formatted for display. Rebuilt on every refresh.</summary>
public class AiToolCardViewModel
{
    private AiToolCardViewModel(string name, string letter, AiToolUsage usage, string usagePageUrl)
    {
        Name = name;
        Letter = letter;
        IsSignedIn = usage.IsSignedIn;
        Account = usage.Account;
        SignInText = !usage.IsSignedIn ? "Not signed in" : usage.Plan != null ? $"Signed in · {usage.Plan}" : "Signed in";
        Plan = usage.IsSignedIn ? usage.Plan : null;
        UsagePageUrl = usagePageUrl;
        Limits = usage.Limits.Select(l => new LimitRow(l)).ToList();
        BarLimits = Limits.Take(2).ToList();
        HasLocalSessions = usage.HasLocalSessions;
        Periods = usage.Periods.Select(p => new PeriodRow(p)).ToList();
        var modelTotal = Math.Max(1, usage.Models.Sum(m => m.Tokens));
        Models = usage.Models.Take(5).Select(m => new ModelRow(m.Model, (double)m.Tokens / modelTotal, m.Tokens)).ToList();
    }

    public string Name { get; }
    public string Letter { get; }
    public bool IsSignedIn { get; }
    public string SignInText { get; }
    public string? Account { get; }
    public string? Plan { get; }

    /// <summary>The command that signs in, run in a console window; null when there's nothing to run from here.</summary>
    public string? SignInCommand { get; private init; }
    public string? SignInHint { get; private init; }

    /// <summary>Where to get the tool, when it isn't installed and isn't signed in.</summary>
    public string? InstallUrl { get; private init; }

    public string UsagePageUrl { get; }
    public IReadOnlyList<LimitRow> Limits { get; }
    public string LimitsNote { get; private init; } = string.Empty;

    /// <summary>The limits the usage bar at the top of the dashboard shows: the session and the week.</summary>
    public IReadOnlyList<LimitRow> BarLimits { get; }

    /// <summary>What the usage bar shows in place of the limits when there are none.</summary>
    public string BarNote => Limits.Count > 0 ? string.Empty
        : InstallUrl != null ? "Not installed"
        : !IsSignedIn ? "Not signed in"
        : "No plan limits yet";
    public bool HasLocalSessions { get; }
    public string NoSessionsText { get; private init; } = string.Empty;
    public IReadOnlyList<PeriodRow> Periods { get; }
    public IReadOnlyList<ModelRow> Models { get; }

    // cliPath is null when Claude Code isn't installed; the sign-in command needs it, so the card offers the install page instead.
    public static AiToolCardViewModel ForClaude(AiToolUsage usage, string usagePageUrl, string? cliPath) => new("Claude", "C", usage, usagePageUrl)
    {
        SignInCommand = usage.IsSignedIn || cliPath == null ? null : AgentCli.ConsoleCommand(cliPath, "auth login"),
        InstallUrl = usage.IsSignedIn || cliPath != null ? null : AgentCli.ClaudeInstallUrl,
        SignInHint = usage.IsSignedIn ? null
            : cliPath == null ? "Claude Code isn't installed on this PC. Install it to chat with Claude and see its plan limits here."
            : "Sign in to Claude Code to see its plan limits here.",
        LimitsNote = usage.LimitsAsOf is not { } asOf
            ? "Claude Code hasn't fetched your plan limits yet. The usage page has them."
            : cliPath != null
                ? $"Checked with Claude Code {Ago(asOf)}. {CheckedAgainText} The usage page always has the latest."
                : $"Saved by Claude Code {Ago(asOf)}. The usage page always has the latest.",
        NoSessionsText = "No Claude Code sessions on this PC in the last 30 days.",
    };

    public static AiToolCardViewModel ForCodex(AiToolUsage usage, string usagePageUrl, string? cliPath) => new("Codex", "X", usage, usagePageUrl)
    {
        SignInCommand = usage.IsSignedIn || cliPath == null ? null : AgentCli.ConsoleCommand(cliPath, "login"),
        InstallUrl = usage.IsSignedIn || cliPath != null ? null : AgentCli.CodexInstallUrl,
        SignInHint = usage.IsSignedIn ? null
            : cliPath == null ? "Codex isn't installed on this PC. Install the Codex app or Codex CLI to chat with Codex and see its use here."
            : "Sign in to Codex to see its plan limits here.",
        LimitsNote = usage.LimitsAsOf is not { } asOf
            ? "Codex hasn't reported your plan limits yet. The usage page has them."
            : cliPath != null
                ? $"Checked with Codex {Ago(asOf)}. {CheckedAgainText} The usage page always has the latest."
                : $"From your last Codex session, {Ago(asOf)}. The usage page always has the latest.",
        NoSessionsText = "No Codex sessions on this PC in the last 30 days.",
    };

    public static readonly TimeSpan CheckLimitsEvery = TimeSpan.FromMinutes(5);
    private static string CheckedAgainText => $"Checked again every {CheckLimitsEvery.TotalMinutes:0} minutes while the dashboard is open.";

    public static string Ago(DateTimeOffset time)
    {
        var age = DateTimeOffset.Now - time;
        return age.TotalMinutes < 1 ? "just now"
            : age.TotalHours < 1 ? Plural((int)age.TotalMinutes, "minute") + " ago"
            : age.TotalDays < 1 ? Plural((int)age.TotalHours, "hour") + " ago"
            : Plural((int)age.TotalDays, "day") + " ago";
    }

    /// <summary>1234 -> "1,234", 45600 -> "45.6K", 3200000 -> "3.2M".</summary>
    public static string Short(long tokens) => tokens switch
    {
        < 10_000 => tokens.ToString("N0"),
        < 1_000_000 => $"{tokens / 1e3:0.#}K",
        < 1_000_000_000 => $"{tokens / 1e6:0.#}M",
        _ => $"{tokens / 1e9:0.##}B",
    };

    private static string Plural(int count, string unit) => $"{count} {unit}{(count == 1 ? "" : "s")}";
}

public class LimitRow
{
    public LimitRow(UsageLimit limit)
    {
        Label = limit.Label;
        ShortLabel = limit.Label switch
        {
            "Session (5 hours)" => "5-hour",
            "Week (all models)" => "Week",
            _ => limit.Label,
        };
        Fraction = Math.Clamp(limit.Percent / 100, 0, 1);
        PercentText = $"{limit.Percent:0}%";
        Level = limit.Percent >= 90 ? "Full" : limit.Percent >= 70 ? "High" : "Normal";

        // A window whose reset has passed started over since the figure was saved, so the figure no longer says much.
        var now = DateTimeOffset.Now;
        IsOutdated = limit.ResetsAt is { } passed && passed <= now;
        Detail = limit.ResetsAt switch
        {
            null => limit.Percent == 0 ? "Not started" : string.Empty,
            { } reset when reset <= now => $"Started over {When(reset)}; newer use not saved yet",
            { } reset => $"Resets {When(reset)}",
        };
        ToolTip = Detail.Length > 0 ? $"{Label}: {PercentText} used\n{Detail}" : $"{Label}: {PercentText} used";
    }

    public string Label { get; }
    public string ShortLabel { get; }
    public double Fraction { get; }
    public string PercentText { get; }
    public string Level { get; }
    public bool IsOutdated { get; }
    public string Detail { get; }
    public string ToolTip { get; }

    private static string When(DateTimeOffset time)
    {
        var local = time.ToLocalTime();
        var days = (local.Date - DateTime.Today).TotalDays;
        return days switch
        {
            0 => $"today {local:t}",
            1 => $"tomorrow {local:t}",
            -1 => $"yesterday {local:t}",
            > 1 and < 7 => $"{local:ddd} {local:t}",
            _ => $"{local:d MMM} {local:t}",
        };
    }
}

public class PeriodRow(UsagePeriod period)
{
    public string Label { get; } = period.Label;
    public string Requests { get; } = period.Requests.ToString("N0");
    public string Input { get; } = AiToolCardViewModel.Short(period.Tokens.Input);
    public string Output { get; } = AiToolCardViewModel.Short(period.Tokens.Output);
    public string Cache { get; } = AiToolCardViewModel.Short(period.Tokens.CacheRead + period.Tokens.CacheWrite);
    public string CacheDetail { get; } =
        $"Read from cache: {period.Tokens.CacheRead:N0}\nWritten to cache: {period.Tokens.CacheWrite:N0}";
    public string Total { get; } = AiToolCardViewModel.Short(period.Tokens.Total);
}

public class ModelRow(string model, double share, long tokens)
{
    public string Model { get; } = model;
    public double Share { get; } = share;
    public string ShareText { get; } = $"{share:P0}";
    public string Tokens { get; } = AiToolCardViewModel.Short(tokens);
}
