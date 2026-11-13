using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoNLauncher.Models;
using RepoNLauncher.Services;

namespace RepoNLauncher.ViewModels;

/// <summary>A date or time format to pick in Settings: Windows' own (no pattern), a preset pattern, or the user's own.</summary>
public sealed class FormatChoice(string example, string note, string? pattern, bool isCustom = false)
{
    /// <summary>How a sample date looks in it, which is what the list shows first.</summary>
    public string Example { get; } = example;
    public string Note { get; } = note;
    public string? Pattern { get; } = pattern;
    public bool IsCustom { get; } = isCustom;
}

/// <summary>
/// The Settings window: the date and time format (applied and saved as it changes), plus the app's other settings,
/// the Android emulators on this PC, its version and updates, and where it comes from.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    // Day past 12 and an afternoon hour, so day/month order and 12/24-hour read at a glance.
    private static readonly DateTime Sample = new(2026, 3, 25, 14, 5, 9);

    private static readonly string[] DatePresets =
        ["yyyy-MM-dd", "dd/MM/yyyy", "MM/dd/yyyy", "dd.MM.yyyy", "d MMM yyyy", "MMM d, yyyy", "ddd d MMM yyyy"];

    private static readonly (string Pattern, string Note)[] TimePresets =
    [
        ("HH:mm", "24-hour"),
        ("HH:mm:ss", "24-hour, seconds"),
        ("h:mm tt", "12-hour"),
        ("h:mm:ss tt", "12-hour, seconds"),
    ];

    private readonly Action<DateTimeFormatSettings> _saveFormat;
    private readonly Action<AppTheme> _saveTheme;
    private readonly bool _loading;

    public SettingsViewModel(DateTimeFormatSettings format, Action<DateTimeFormatSettings> saveFormat, UpdateViewModel updates,
        GitHubAccountViewModel gitHub, ObservableCollection<OpenWithApp> openWithApps, EmulatorsViewModel emulators,
        SiteBrowserViewModel siteBrowser, AppTheme theme, Action<AppTheme> saveTheme)
    {
        _saveFormat = saveFormat;
        _saveTheme = saveTheme;
        _theme = theme;
        _isLightTheme = ThemeService.IsLight;
        // Matching Windows, the palette changes without the setting changing
        ThemeService.Changed += (_, _) => IsLightTheme = ThemeService.IsLight;
        Updates = updates;
        Emulators = emulators;
        SiteBrowser = siteBrowser;
        GitHub = gitHub;
        OpenWithApps = openWithApps;

        DateChoices =
        [
            new FormatChoice(DateTimeDisplay.DateWith(Sample, null), "Windows' format", null),
            .. DatePresets.Select(p => new FormatChoice(DateTimeDisplay.DateWith(Sample, p), p, p)),
            new FormatChoice("Custom", "your own pattern", null, isCustom: true),
        ];
        TimeChoices =
        [
            new FormatChoice(DateTimeDisplay.TimeWith(Sample, null), "Windows' format", null),
            .. TimePresets.Select(p => new FormatChoice(DateTimeDisplay.TimeWith(Sample, p.Pattern), p.Note, p.Pattern)),
            new FormatChoice("Custom", "your own pattern", null, isCustom: true),
        ];

        _loading = true;
        (DateChoice, CustomDatePattern) = Pick(DateChoices, format.DatePattern);
        (TimeChoice, CustomTimePattern) = Pick(TimeChoices, format.TimePattern);
        _loading = false;
        RefreshPreview();
        RefreshDesktopIcon();
    }

    public UpdateViewModel Updates { get; }

    /// <summary>The Emulators tab: the same as the Android emulators window.</summary>
    public EmulatorsViewModel Emulators { get; }

    /// <summary>Where a project's site opens: the same choice as the Run tab's Open in.</summary>
    public SiteBrowserViewModel SiteBrowser { get; }
    public GitHubAccountViewModel GitHub { get; }
    public ObservableCollection<OpenWithApp> OpenWithApps { get; }

    // --- Date & time ---

    public IReadOnlyList<FormatChoice> DateChoices { get; }
    public IReadOnlyList<FormatChoice> TimeChoices { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomDate), nameof(ShowFormatCodes))]
    private FormatChoice _dateChoice = null!;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomTime), nameof(ShowFormatCodes))]
    private FormatChoice _timeChoice = null!;

    [ObservableProperty] private string _customDatePattern = string.Empty;
    [ObservableProperty] private string _customTimePattern = string.Empty;
    [ObservableProperty] private string? _dateError;
    [ObservableProperty] private string? _timeError;

    /// <summary>The time now, in the format picked.</summary>
    [ObservableProperty] private string _preview = string.Empty;

    public void RefreshPreview() => Preview = DateTimeDisplay.DateAndTime(DateTime.Now);

    public bool IsCustomDate => DateChoice.IsCustom;
    public bool IsCustomTime => TimeChoice.IsCustom;
    public bool ShowFormatCodes => IsCustomDate || IsCustomTime;

    // Switching to Custom starts from the format that was picked, ready to change.
    partial void OnDateChoiceChanged(FormatChoice? oldValue, FormatChoice newValue)
    {
        if (!_loading && newValue.IsCustom && string.IsNullOrWhiteSpace(CustomDatePattern))
            CustomDatePattern = oldValue?.Pattern ?? DateTimeDisplay.SystemDatePattern;
        FormatChanged();
    }

    partial void OnTimeChoiceChanged(FormatChoice? oldValue, FormatChoice newValue)
    {
        if (!_loading && newValue.IsCustom && string.IsNullOrWhiteSpace(CustomTimePattern))
            CustomTimePattern = oldValue?.Pattern ?? DateTimeDisplay.SystemTimePattern;
        FormatChanged();
    }

    partial void OnCustomDatePatternChanged(string value) => FormatChanged();
    partial void OnCustomTimePatternChanged(string value) => FormatChanged();

    [RelayCommand]
    private void ResetFormat()
    {
        DateChoice = DateChoices[0];
        TimeChoice = TimeChoices[0];
    }

    // Applied and saved as it changes, so the dashboard behind the window shows it straight away. A custom pattern
    // that can't be used yet (half typed, say) leaves the last good format in place.
    private void FormatChanged()
    {
        if (_loading) return;

        var date = Resolve(DateChoice, CustomDatePattern, "yyyy-MM-dd", out var dateError);
        var time = Resolve(TimeChoice, CustomTimePattern, "HH:mm", out var timeError);
        DateError = dateError;
        TimeError = timeError;
        if (dateError != null || timeError != null) return;

        var settings = new DateTimeFormatSettings { DatePattern = date, TimePattern = time };
        DateTimeDisplay.Apply(settings);
        _saveFormat(settings);
        RefreshPreview();
    }

    private static string? Resolve(FormatChoice choice, string custom, string example, out string? error)
    {
        error = null;
        if (!choice.IsCustom) return choice.Pattern;

        var pattern = custom.Trim();
        if (pattern.Length == 0) error = $"Type a pattern, such as {example}.";
        else if (!DateTimeDisplay.IsValid(pattern)) error = "That pattern can't be read. Check its quotes, % and \\ signs.";
        return pattern;
    }

    private static (FormatChoice Choice, string Custom) Pick(IReadOnlyList<FormatChoice> choices, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || !DateTimeDisplay.IsValid(pattern)) return (choices[0], string.Empty);
        var preset = choices.FirstOrDefault(c => !c.IsCustom && c.Pattern == pattern);
        return preset != null ? (preset, string.Empty) : (choices[^1], pattern);
    }

    // --- General ---

    /// <summary>Dark, light, or as Windows says. Every open window takes it straight away, and it's saved.</summary>
    [ObservableProperty] private AppTheme _theme;

    /// <summary>The light palette is showing, so the header's button offers dark.</summary>
    [ObservableProperty] private bool _isLightTheme;

    partial void OnThemeChanged(AppTheme value)
    {
        ThemeService.Apply(value);
        _saveTheme(value);
    }

    /// <summary>The header's button: whichever of dark and light isn't showing, which also stops matching Windows.</summary>
    [RelayCommand]
    private void ToggleTheme() => Theme = ThemeService.IsLight ? AppTheme.Dark : AppTheme.Light;

    public string SettingsFolder => DashboardConfigService.AppDataDir;

    /// <summary>Only the installed app has a desktop icon of its own to add.</summary>
    public bool CanHaveDesktopIcon => Updates.CanUpdate;

    [ObservableProperty] private bool _hasDesktopIcon;
    [ObservableProperty] private string? _desktopIconError;

    public void RefreshDesktopIcon() => HasDesktopIcon = CanHaveDesktopIcon && DesktopIcon.Exists;

    [RelayCommand]
    private void CreateDesktopIcon()
    {
        try
        {
            DesktopIcon.Create();
            DesktopIconError = null;
        }
        catch (Exception e)
        {
            DesktopIconError = $"The desktop icon couldn't be created: {e.Message}";
        }
        RefreshDesktopIcon();
    }

    [RelayCommand]
    private void OpenSettingsFolder() => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{SettingsFolder}\"") { UseShellExecute = true });

    // --- About ---

    public string AppName => AppInfo.Name;
    public string Description => AppInfo.Description;
    public string Author => AppInfo.Author;
    public string AuthorHandle => $"@{AppInfo.AuthorLogin}";
    public string AuthorUrl => AppInfo.AuthorUrl;
    public string RepoUrl => AppInfo.RepoUrl;
    public string RepoText => AppInfo.RepoUrl.Replace("https://", "");
    public string IssuesUrl => AppInfo.IssuesUrl;
    public string ReleasesUrl => AppInfo.ReleasesUrl;
    public string License => AppInfo.License;
    public string LicenseUrl => AppInfo.LicenseUrl;
    public string FormatCodesUrl => AppInfo.FormatCodesUrl;

    public string Runtime =>
        $".NET {Environment.Version} · {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}";

    /// <summary>For a bug report: the version, how it was installed, and what it runs on.</summary>
    public string VersionInfo =>
        $"{AppInfo.Name} {Updates.VersionText} ({InstallKind})\n{Runtime}";

    private string InstallKind => !Updates.CanUpdate ? "not installed" : Updates.IsInstalledBySetup ? "installed" : "portable";

    [ObservableProperty] private string? _copyNote;

    [RelayCommand]
    private void CopyVersionInfo()
    {
        try
        {
            System.Windows.Clipboard.SetText(VersionInfo);
            CopyNote = "Copied";
        }
        catch (COMException)
        {
            CopyNote = "Couldn't copy: another program is using the clipboard";
        }
    }

    [RelayCommand]
    private void OpenLink(string? url)
    {
        if (!string.IsNullOrEmpty(url)) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
