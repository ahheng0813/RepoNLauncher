using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoNLauncher.Services;

namespace RepoNLauncher.ViewModels;

public enum RepoFilter
{
    All,
    Mine,
    Others,
}

/// <summary>
/// Repos on this PC: finds every git repo on the local drives, asks GitHub who owns each, and lets the user add the
/// ones they pick to the dashboard. Needs a GitHub sign-in; results are kept for the session (and cleared when the
/// account changes, since "mine" depends on it).
/// </summary>
public partial class PcReposViewModel : ObservableObject
{
    private const int ParallelGitHubRequests = 8;

    private readonly IGitHubService _gitHub;
    private readonly Func<string, bool> _isOnDashboard;
    private readonly Func<IReadOnlyList<string>, int> _addToDashboard;
    private CancellationTokenSource? _scan;
    private string? _resultsAccount;

    public PcReposViewModel(GitHubAccountViewModel account, Func<string, bool> isOnDashboard, Func<IReadOnlyList<string>, int> addToDashboard)
    {
        Account = account;
        _gitHub = account.Service;
        _isOnDashboard = isOnDashboard;
        _addToDashboard = addToDashboard;

        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(Repos);
        view.Filter = item => IsShown((PcRepoViewModel)item);
        // Repos move between Mine and Others as GitHub answers, so the filter is re-applied as their ownership changes.
        view.IsLiveFiltering = true;
        view.LiveFilteringProperties.Add(nameof(PcRepoViewModel.Ownership));
        VisibleRepos = view;

        _gitHub.SignInChanged += OnSignInChanged;
        _status = StatusWhenIdle();
    }

    public GitHubAccountViewModel Account { get; }

    public ObservableCollection<PcRepoViewModel> Repos { get; } = new();

    /// <summary><see cref="Repos"/> narrowed to the chosen <see cref="Filter"/>.</summary>
    public ICollectionView VisibleRepos { get; }

    [ObservableProperty] private RepoFilter _filter;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private bool _isScanning;

    [ObservableProperty] private string _status;

    public int MineCount => Repos.Count(r => r.Ownership == RepoOwnership.Mine);
    public int OthersCount => Repos.Count(r => r.Ownership == RepoOwnership.Others);
    public int CheckedCount => Repos.Count(r => r.IsChecked);

    /// <summary>The "select all" box: ticks every shown repo not already on the dashboard; null when only some are.</summary>
    public bool? AllShownChecked
    {
        get
        {
            var addable = Shown().Where(r => !r.IsOnDashboard).ToList();
            if (addable.Count == 0 || addable.All(r => !r.IsChecked)) return false;
            return addable.All(r => r.IsChecked) ? true : null;
        }
        set
        {
            foreach (var repo in Shown().Where(r => !r.IsOnDashboard)) repo.IsChecked = value == true;
        }
    }

    partial void OnFilterChanged(RepoFilter value)
    {
        VisibleRepos.Refresh();
        OnPropertyChanged(nameof(AllShownChecked));
    }

    private bool CanScan() => !IsScanning && Account.IsSignedIn;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        var login = _gitHub.Login!;
        using var scan = _scan = new CancellationTokenSource();
        IsScanning = true;
        Repos.Clear();
        RaiseCounts();
        _resultsAccount = login;
        try
        {
            Status = "Looking for repos on this PC…";
            var walking = true; // progress reports arrive late; once the walk is over they'd overwrite newer status
            var progress = new Progress<PcScanProgress>(p =>
            {
                if (walking) Status = $"Looking for repos… {p.FoldersChecked:N0} folders checked, {p.ReposFound} found";
            });
            var found = await Task.Run(
                () => PcRepoFinder.FindAll(progress, scan.Token).Select(path => (Path: path, Remote: GitHubRemote.Find(path))).ToList(),
                scan.Token);
            walking = false;

            foreach (var (path, remote) in found)
                Add(new PcRepoViewModel(path, remote) { IsOnDashboard = _isOnDashboard(path) });
            RaiseCounts();

            await CheckOwnersAsync(login, scan.Token);
            Status = Summary();
        }
        catch (OperationCanceledException)
        {
            StopChecking();
            Status = Repos.Count == 0 ? "Scan stopped." : $"Scan stopped. {Summary()}";
        }
        catch (GitHubException ex)
        {
            StopChecking();
            Status = ex.Message;
        }
        finally
        {
            _scan = null;
            IsScanning = false;
        }
    }

    [RelayCommand]
    private void StopScan() => _scan?.Cancel();

    private bool CanAddCheckedToDashboard() => CheckedCount > 0;

    [RelayCommand(CanExecute = nameof(CanAddCheckedToDashboard))]
    private void AddCheckedToDashboard()
    {
        var picked = Repos.Where(r => r.IsChecked && !r.IsOnDashboard).ToList();
        var added = _addToDashboard(picked.Select(r => r.Path).ToList());
        foreach (var repo in picked)
        {
            repo.IsChecked = false;
            repo.IsOnDashboard = true;
        }
        Status = $"Added {added} repo{(added == 1 ? "" : "s")} to the dashboard.";
    }

    /// <summary>Re-reads which repos the dashboard has, since projects can be removed there while this window is open.</summary>
    public void RefreshDashboardFlags()
    {
        foreach (var repo in Repos)
        {
            repo.IsOnDashboard = _isOnDashboard(repo.Path);
            if (repo.IsOnDashboard) repo.IsChecked = false;
        }
        OnPropertyChanged(nameof(AllShownChecked));
    }

    // Clones of the same repo are asked about once; a few requests run at a time.
    private async Task CheckOwnersAsync(string login, CancellationToken ct)
    {
        var toCheck = Repos
            .Where(r => r.Ownership == RepoOwnership.Checking)
            .GroupBy(r => r.Remote!.Value, RemoteComparer.Instance)
            .ToList();
        if (toCheck.Count == 0) return;

        var done = 0;
        Status = $"Asking GitHub who owns each repo… 0 of {toCheck.Count}";
        using var limit = new SemaphoreSlim(ParallelGitHubRequests);
        await Task.WhenAll(toCheck.Select(async clones =>
        {
            await limit.WaitAsync(ct);
            try
            {
                var answer = await _gitHub.GetRepoAsync(clones.Key.Owner, clones.Key.Name, ct);
                foreach (var clone in clones) clone.ApplyGitHubAnswer(answer, login);
            }
            finally
            {
                limit.Release();
            }
            Status = $"Asking GitHub who owns each repo… {++done} of {toCheck.Count}";
        }));
    }

    private void StopChecking()
    {
        foreach (var repo in Repos.Where(r => r.Ownership == RepoOwnership.Checking))
            repo.Ownership = RepoOwnership.NotChecked;
    }

    private void Add(PcRepoViewModel repo)
    {
        repo.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PcRepoViewModel.Ownership) or nameof(PcRepoViewModel.IsChecked) or nameof(PcRepoViewModel.IsOnDashboard))
                RaiseCounts();
        };
        Repos.Add(repo);
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(MineCount));
        OnPropertyChanged(nameof(OthersCount));
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(AllShownChecked));
        AddCheckedToDashboardCommand.NotifyCanExecuteChanged();
    }

    private bool IsShown(PcRepoViewModel repo) => Filter switch
    {
        RepoFilter.Mine => repo.Ownership == RepoOwnership.Mine,
        RepoFilter.Others => repo.Ownership == RepoOwnership.Others,
        _ => true,
    };

    private IEnumerable<PcRepoViewModel> Shown() => Repos.Where(IsShown);

    private string Summary()
    {
        var notOnGitHub = Repos.Count(r => r.Ownership == RepoOwnership.NotOnGitHub);
        var noAccess = Repos.Count(r => r.Ownership == RepoOwnership.NoAccess);
        var summary = $"Found {Repos.Count} repos: {MineCount} yours, {OthersCount} owned by others, {notOnGitHub} not on GitHub";
        return noAccess > 0 ? $"{summary}, {noAccess} GitHub won't show you." : $"{summary}.";
    }

    private string StatusWhenIdle() => Account.IsSignedIn
        ? "Press Scan PC to find every repo on this PC."
        : "Sign in to GitHub to find your repos.";

    private void OnSignInChanged()
    {
        ScanCommand.NotifyCanExecuteChanged();
        if (_gitHub.Login == _resultsAccount) return;

        // The results say whose repos are "mine", so they don't carry over to another account (or to signed out).
        _scan?.Cancel();
        Repos.Clear();
        RaiseCounts();
        _resultsAccount = null;
        Status = StatusWhenIdle();
    }

    private sealed class RemoteComparer : IEqualityComparer<(string Owner, string Name)>
    {
        public static readonly RemoteComparer Instance = new();

        public bool Equals((string Owner, string Name) x, (string Owner, string Name) y) =>
            string.Equals(x.Owner, y.Owner, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Owner, string Name) remote) =>
            HashCode.Combine(remote.Owner.ToUpperInvariant(), remote.Name.ToUpperInvariant());
    }
}
