using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoNLauncher.Services;

namespace RepoNLauncher.ViewModels;

/// <summary>
/// The dashboard browser's ad blocker, shared by every browser window: on or off and how strong (Settings, and the
/// shield beside a browser window's address, both saved), the sites it's paused on until the app closes, and the
/// rules it blocks with, read from the filter lists it keeps up to date.
/// </summary>
public partial class AdBlockerViewModel : ObservableObject
{
    private readonly Action<bool, AdBlockLevel> _save;
    private readonly HashSet<string> _paused = new(StringComparer.Ordinal);
    private Task? _ready;
    private int _buildVersion;
    private string? _downloadError;

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private AdBlockLevel _level;

    /// <summary>What it blocks with, and when the lists were downloaded, or why they couldn't be.</summary>
    [ObservableProperty] private string _listsStatus = "The filter lists are downloaded when the dashboard browser first opens.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateListsCommand))]
    private bool _isUpdatingLists;

    /// <summary>The sites it's paused on, until the app closes.</summary>
    public ObservableCollection<string> PausedSites { get; } = new();

    /// <summary>The rules for the strength picked; empty until they've been read.</summary>
    public AdFilterEngine Engine { get; private set; } = AdFilterEngine.Empty;

    /// <summary>Turned on or off, made stronger or weaker, paused or resumed on a site, or the rules were read again.</summary>
    public event Action? Changed;

    public AdBlockerViewModel(bool isEnabled, AdBlockLevel level, Action<bool, AdBlockLevel> save)
    {
        _isEnabled = isEnabled;
        _level = Enum.IsDefined(level) ? level : AdBlockLevel.Balanced;
        _save = save;
    }

    partial void OnIsEnabledChanged(bool value)
    {
        _save(value, Level);
        if (value) _ = WhenReadyAsync();
        RefreshStatus();
        Changed?.Invoke();
    }

    partial void OnLevelChanged(AdBlockLevel value)
    {
        _save(IsEnabled, value);
        // The rules read now are the old strength's until the new ones are: the lists it adds may still need downloading.
        if (_ready != null) _ready = LoadAsync(forceDownload: false);
        Changed?.Invoke();
    }

    /// <summary>
    /// Once the rules are read, the first time from the lists kept on this PC, then the lists are fetched again in the
    /// background when they're missing or old. A browser window waits for it before it shows its first page.
    /// </summary>
    public Task WhenReadyAsync()
    {
        if (!IsEnabled) return Task.CompletedTask;
        return _ready ??= LoadAsync(forceDownload: false);
    }

    // Reads the rules from the lists kept, then downloads the stale ones and reads them again; the window waits only for
    // the first read. A strength changed meanwhile is read once this one's done.
    private async Task LoadAsync(bool forceDownload)
    {
        await RebuildAsync();
        _ = DownloadAsync(forceDownload);
    }

    [RelayCommand(CanExecute = nameof(CanUpdateLists))]
    private Task UpdateLists() => DownloadAsync(force: true);

    private bool CanUpdateLists() => !IsUpdatingLists;

    private async Task DownloadAsync(bool force)
    {
        if (IsUpdatingLists) return;
        IsUpdatingLists = true;
        try
        {
            AdBlockLevel level;
            do
            {
                level = Level;
                var due = AdFilterLists.For(level).Where(l => force || AdFilterLists.IsStale(l)).ToList();
                if (due.Count == 0) break;

                ListsStatus = due.Count == 1 ? $"Downloading {due[0].Name}…" : "Downloading the filter lists…";
                _downloadError = null;
                foreach (var list in due)
                {
                    try
                    {
                        await AdFilterLists.DownloadAsync(list);
                    }
                    catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException
                                                  or UnauthorizedAccessException or InvalidDataException)
                    {
                        var reason = e is TaskCanceledException ? "it took too long" : e.Message.TrimEnd('.');
                        _downloadError ??= $"{list.Name} couldn't be downloaded ({reason}).";
                    }
                }
                await RebuildAsync();
                force = false;
            }
            while (level != Level);
        }
        finally
        {
            IsUpdatingLists = false;
            RefreshStatus();
        }
    }

    private IReadOnlyList<AdFilterList> _read = [];

    private async Task RebuildAsync()
    {
        var version = ++_buildVersion;
        var level = Level;
        var (engine, read) = await Task.Run(() =>
        {
            var built = AdFilterLists.Build(level, out var lists);
            return (built, lists);
        });
        if (version != _buildVersion) return; // another strength picked since: its rules are on their way

        Engine = engine;
        _read = read;
        RefreshStatus();
        Changed?.Invoke();
    }

    private void RefreshStatus()
    {
        if (IsUpdatingLists) return;
        if (_ready == null)
        {
            ListsStatus = "The filter lists are downloaded when the dashboard browser first opens.";
            return;
        }

        string status;
        if (_read.Count == 0)
        {
            status = $"Blocking with the built-in list of the best-known ad servers ({Engine.RuleCount:N0} rules) until the filter lists download.";
        }
        else
        {
            var oldest = _read.Select(AdFilterLists.UpdatedAt).Min() ?? DateTime.Now;
            status = $"{Engine.RuleCount:N0} rules from {string.Join(", ", _read.Select(l => l.Name))}. Downloaded {DateTimeDisplay.DateAndTime(oldest)}.";
        }
        ListsStatus = _downloadError != null ? $"{_downloadError} {status}" : status;
    }

    // ---- Paused sites ----

    /// <summary>The site a page belongs to, as it's paused: example.com for www.example.com.</summary>
    public static string SiteOf(string host) => AdFilterEngine.SiteOf(host);

    public bool IsPaused(string host) => host.Length > 0 && _paused.Contains(SiteOf(host));

    /// <summary>Pauses it on the page's site (and every page of it) until the app closes, or resumes it there.</summary>
    public void SetPaused(string host, bool paused)
    {
        var site = SiteOf(host);
        if (site.Length == 0 || !(paused ? _paused.Add(site) : _paused.Remove(site))) return;
        if (paused) PausedSites.Add(site);
        else PausedSites.Remove(site);
        Changed?.Invoke();
    }

    [RelayCommand]
    private void Resume(string? site)
    {
        if (site != null) SetPaused(site, false);
    }

    /// <summary>
    /// A site served from this PC or its network, such as localhost:3000: one of the user's own projects, which the
    /// ad blocker leaves as it is, as an app's own requests being blocked would look like its bug.
    /// </summary>
    public static bool IsLocal(string host) => AddressInput.IsLocalHost(host);

    /// <summary>Whether it blocks on the page at this host: on, not paused there, not a site of this PC's, and not one the lists let alone.</summary>
    public bool Filters(string pageHost) =>
        IsEnabled && pageHost.Length > 0 && !IsLocal(pageHost) && !IsPaused(pageHost) && !Engine.AllowsPage(pageHost);
}
