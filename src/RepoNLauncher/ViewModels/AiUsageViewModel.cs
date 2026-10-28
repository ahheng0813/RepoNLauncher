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
    public async Task RefreshAsync()
    {
        // The readers keep what they've parsed between calls and aren't thread-safe, so one refresh at a time.
        if (IsRefreshing) return;
        IsRefreshing = true;
        try
        {
            var (claude, codex, claudeCli, codexCli) = await Task.Run(() =>
                (_claude.Read(), _codex.Read(), AgentCli.FindClaude(), AgentCli.FindCodex()));
            ClaudeCard = AiToolCardViewModel.ForClaude(claude, _claude.UsagePageUrl, claudeCli);
            CodexCard = AiToolCardViewModel.ForCodex(codex, _codex.UsagePageUrl, codexCli);
            Status = $"Updated {DateTime.Now:t}. Refreshes every minute while this window is open.";
        }
        finally
        {
            IsRefreshing = false;
        }
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
        UsagePageUrl = usagePageUrl;
        Limits = usage.Limits.Select(l => new LimitRow(l)).ToList();
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

    /// <summary>The command that signs in, run in a console window; null when there's nothing to run from here.</summary>
    public string? SignInCommand { get; private init; }
    public string? SignInHint { get; private init; }

    /// <summary>Where to get the tool, when it isn't installed and isn't signed in.</summary>
    public string? InstallUrl { get; private init; }

    public string UsagePageUrl { get; }
    public IReadOnlyList<LimitRow> Limits { get; }
    public string LimitsNote { get; private init; } = string.Empty;
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
        LimitsNote = usage.LimitsAsOf is { } asOf
            ? $"Saved by Claude Code {Ago(asOf)}. It refreshes them when you use the claude CLI; the usage page always has the latest."
            : "Claude Code hasn't saved your plan limits yet. They show here after you use the claude CLI.",
        NoSessionsText = "No Claude Code sessions on this PC in the last 30 days.",
    };

    public static AiToolCardViewModel ForCodex(AiToolUsage usage, string usagePageUrl, string? cliPath) => new("Codex", "X", usage, usagePageUrl)
    {
        SignInCommand = usage.IsSignedIn || cliPath == null ? null : AgentCli.ConsoleCommand(cliPath, "login"),
        InstallUrl = usage.IsSignedIn || cliPath != null ? null : AgentCli.CodexInstallUrl,
        SignInHint = usage.IsSignedIn ? null
            : cliPath == null ? "Codex isn't installed on this PC. Install the Codex app or Codex CLI to chat with Codex and see its use here."
            : "Sign in to Codex to see its plan limits here.",
        LimitsNote = usage.LimitsAsOf is { } asOf
            ? $"From your last Codex CLI or IDE session, {Ago(asOf)}. The usage page always has the latest."
            : "No plan limits saved on this PC yet. They show here after you use Codex CLI or the IDE extension.",
        NoSessionsText = "No Codex CLI or IDE sessions on this PC in the last 30 days. The Codex app keeps its sessions in the cloud, so its use shows on the usage page.",
    };

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
    }

    public string Label { get; }
    public double Fraction { get; }
    public string PercentText { get; }
    public string Level { get; }
    public bool IsOutdated { get; }
    public string Detail { get; }

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
