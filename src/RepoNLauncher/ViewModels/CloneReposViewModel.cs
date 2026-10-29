using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoNLauncher.Services;

namespace RepoNLauncher.ViewModels;

public enum CloneFilter
{
    All,
    NotOnPc,
    OnPc,
}

/// <summary>
/// Clone from GitHub: lists every repo the GitHub account can see, finds which of them this PC has a clone of, and
/// clones the ones picked, each on the branch picked for it, into one folder. Results are kept for the session (and
/// cleared when the account changes); cloning keeps going if the window is closed.
/// </summary>
public partial class CloneReposViewModel : ObservableObject
{
    private readonly IGitService _git;
    private readonly IGitHubService _gitHub;
    private readonly Func<string, bool> _isOnDashboard;
    private readonly Func<IReadOnlyList<string>, int> _addToDashboard;
    private readonly Func<IEnumerable<string>> _dashboardPaths;
    private readonly Action<string> _saveCloneFolder;

    // The GitHub repo ("owner/name") of each repo found on the PC by the last look through it; null until that's done.
    private List<(string Path, string FullName)>? _pcClones;
    private readonly List<string> _clonedHere = new();
    private CancellationTokenSource? _load;
    private CancellationTokenSource? _cloning;
    private string? _resultsAccount;

    public CloneReposViewModel(
        GitHubAccountViewModel account,
        IGitService git,
        Func<string, bool> isOnDashboard,
        Func<IReadOnlyList<string>, int> addToDashboard,
        Func<IEnumerable<string>> dashboardPaths,
        string cloneFolder,
        Action<string> saveCloneFolder)
    {
        Account = account;
        _git = git;
        _gitHub = account.Service;
        _isOnDashboard = isOnDashboard;
        _addToDashboard = addToDashboard;
        _dashboardPaths = dashboardPaths;
        _cloneFolder = cloneFolder;
        _saveCloneFolder = saveCloneFolder;

        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(Repos);
        view.Filter = item => IsShown((CloneRepoViewModel)item);
        // A repo moves to On this PC once it's cloned, so the filter is re-applied as that changes.
        view.IsLiveFiltering = true;
        view.LiveFilteringProperties.Add(nameof(CloneRepoViewModel.IsOnPc));
        VisibleRepos = view;

        _gitHub.SignInChanged += OnSignInChanged;
        _status = StatusWhenIdle();
    }

    public GitHubAccountViewModel Account { get; }

    public ObservableCollection<CloneRepoViewModel> Repos { get; } = new();

    /// <summary><see cref="Repos"/> narrowed to the chosen <see cref="Filter"/> and <see cref="Search"/>.</summary>
    public ICollectionView VisibleRepos { get; }

    [ObservableProperty] private CloneFilter _filter;
    [ObservableProperty] private string _search = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(CloneCommand))]
    private bool _isCloning;

    [ObservableProperty] private string _status;

    /// <summary>Where clones go, each in a folder named after its repo.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloneCommand))]
    private string _cloneFolder;

    [ObservableProperty] private bool _addClonesToDashboard = true;

    public int OnPcCount => Repos.Count(r => r.IsOnPc);
    public int NotOnPcCount => Repos.Count - OnPcCount;
    public int CheckedCount => Repos.Count(r => r.IsChecked);

    /// <summary>The "select all" box: ticks every shown repo that can be cloned; null when only some are.</summary>
    public bool? AllShownChecked
    {
        get
        {
            var cloneable = Shown().Where(r => r.CanCheck).ToList();
            if (cloneable.Count == 0 || cloneable.All(r => !r.IsChecked)) return false;
            return cloneable.All(r => r.IsChecked) ? true : null;
        }
        set
        {
            foreach (var repo in Shown().Where(r => r.CanCheck)) repo.IsChecked = value == true;
        }
    }

    partial void OnFilterChanged(CloneFilter value) => RefreshView();

    partial void OnSearchChanged(string value) => RefreshView();

    // Typed or picked: clones already in the new folder count as on this PC.
    partial void OnCloneFolderChanged(string value) => ApplyLocal();

    private bool CanRefresh() => !IsLoading && !IsCloning && Account.IsSignedIn;

    /// <summary>Gets the list from GitHub, and looks through the PC for clones at the same time.</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        var login = _gitHub.Login!;
        using var load = _load = new CancellationTokenSource();
        IsLoading = true;
        _resultsAccount = login;
        int? listed = null;
        var scanning = true; // progress reports arrive late; once the look is over they'd overwrite newer status
        var progress = new Progress<PcScanProgress>(p =>
        {
            if (scanning && listed is { } count)
                Status = $"{count} repos on GitHub. Looking for them on this PC… {p.FoldersChecked:N0} folders checked";
        });
        var lookingOnPc = FindPcClonesAsync(progress, load.Token);
        try
        {
            Status = "Getting your repos from GitHub…";
            var repos = await _gitHub.GetMyReposAsync(load.Token);
            Repos.Clear();
            foreach (var repo in repos) Add(new CloneRepoViewModel(repo, _gitHub));
            ApplyLocal();
            listed = Repos.Count;
            if (!lookingOnPc.IsCompleted) Status = $"{Repos.Count} repos on GitHub. Looking for them on this PC…";

            _pcClones = await lookingOnPc;
            scanning = false;
            ApplyLocal();
            Status = Summary();
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped.";
        }
        catch (GitHubException ex)
        {
            load.Cancel(); // the look through the PC too
            Status = ex.Message;
        }
        finally
        {
            scanning = false;
            _load = null;
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void BrowseCloneFolder()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Clone into this folder",
            UseDescriptionForTitle = true,
            InitialDirectory = Directory.Exists(CloneFolder) ? CloneFolder : string.Empty,
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) CloneFolder = dialog.SelectedPath;
    }

    private bool CanClone() => !IsCloning && CheckedCount > 0 && CloneFolder.Trim().Length > 0;

    /// <summary>Clones the ticked repos one at a time, so a GitHub sign-in Git asks for is asked once.</summary>
    [RelayCommand(CanExecute = nameof(CanClone))]
    private async Task CloneAsync()
    {
        var folder = CloneFolder.Trim();
        if (!Path.IsPathFullyQualified(folder))
        {
            Status = "Type the full path of the folder to clone into, such as C:\\Projects, or press Browse.";
            return;
        }
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Status = $"Couldn't make the folder {folder}: {ex.Message}";
            return;
        }
        _saveCloneFolder(folder);

        var picked = Repos.Where(r => r.IsChecked && r.CanCheck).ToList();
        foreach (var repo in picked)
        {
            repo.IsChecked = false;
            repo.CloneState = CloneState.Waiting;
            repo.CloneText = "Waiting…";
        }

        using var cloning = _cloning = new CancellationTokenSource();
        IsCloning = true;
        var cloned = new List<string>();
        var failed = 0;
        try
        {
            foreach (var repo in picked)
            {
                cloning.Token.ThrowIfCancellationRequested();
                Status = $"Cloning {repo.FullName}… ({cloned.Count + failed + 1} of {picked.Count})";
                var target = Path.Combine(folder, repo.Repo.Name);
                if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
                {
                    repo.Failed($"There's already a folder called {repo.Repo.Name} in {folder}. Move or rename it, or clone into another folder.");
                    failed++;
                    continue;
                }

                repo.CloneState = CloneState.Cloning;
                repo.CloneText = "Cloning…";
                // The default branch is left to git, which also copes with an empty repo that has no branch yet.
                var branch = repo.Branch.IsDefault ? null : repo.Branch.Name;
                var result = await _git.CloneAsync(repo.Repo.CloneUrl, target, branch, new Progress<string>(repo.ShowProgress), cloning.Token);
                if (result.Success)
                {
                    _clonedHere.Add(target);
                    repo.Cloned(target);
                    cloned.Add(target);
                    // Straight away rather than after the batch, so it's there even if the dashboard is closed mid-batch.
                    if (AddClonesToDashboard)
                    {
                        _addToDashboard([target]);
                        repo.IsOnDashboard = true;
                    }
                }
                else
                {
                    repo.Failed($"Couldn't clone it: {CloneError(result.Output)}");
                    failed++;
                }
            }
            Status = failed == 0
                ? $"Cloned {CountOf(cloned.Count)} into {folder}."
                : $"Cloned {cloned.Count} of {picked.Count} into {folder}. The reason each other one failed is under it.";
        }
        catch (OperationCanceledException)
        {
            Status = $"Stopped. Cloned {cloned.Count} of {picked.Count}.";
        }
        finally
        {
            foreach (var repo in picked.Where(r => r.CloneState is CloneState.Waiting or CloneState.Cloning)) repo.ClearClone();
            _cloning = null;
            IsCloning = false;
            RaiseCounts();
        }
    }

    [RelayCommand]
    private void StopCloning() => _cloning?.Cancel();

    [RelayCommand]
    private void AddToDashboard(CloneRepoViewModel? repo)
    {
        if (repo?.LocalPath is not { } path) return;
        _addToDashboard([path]);
        repo.IsOnDashboard = true;
        Status = $"Added {repo.FullName} to the dashboard.";
    }

    /// <summary>Checks again which repos are on this PC and the dashboard, since either can change while the window is open.</summary>
    public void ApplyLocal()
    {
        var clones = LocalClones();
        foreach (var repo in Repos)
        {
            // A clone being made has its folder already; it counts once it's done.
            if (repo.CloneState is CloneState.Waiting or CloneState.Cloning) continue;
            repo.LocalPath = clones.GetValueOrDefault(repo.FullName);
            repo.IsOnDashboard = repo.LocalPath != null && _isOnDashboard(repo.LocalPath);
            repo.IsPcChecked = _pcClones != null;
            if (repo.IsOnPc) repo.IsChecked = false;
        }
        RaiseCounts();
    }

    // The clone of each GitHub repo ("owner/name", any case, as GitHub matches it) that the PC has: the dashboard's
    // copy first, then ones in the clone folder, then the rest found on the PC.
    private Dictionary<string, string> LocalClones()
    {
        var clones = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string path)
        {
            if (GitHubRemote.Find(path) is { } remote) clones.TryAdd($"{remote.Owner}/{remote.Name}", path);
        }

        foreach (var path in _dashboardPaths().Where(Directory.Exists)) Add(path);
        foreach (var path in _clonedHere) Add(path);
        foreach (var path in ReposIn(CloneFolder.Trim())) Add(path);
        foreach (var (path, fullName) in _pcClones ?? []) clones.TryAdd(fullName, path);
        return clones;
    }

    private static IEnumerable<string> ReposIn(string folder)
    {
        try
        {
            if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder)) return [];
            // .git is a folder in a clone, and a file in a worktree.
            return Directory.EnumerateDirectories(folder)
                .Where(dir => Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git")))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }

    private static Task<List<(string Path, string FullName)>> FindPcClonesAsync(IProgress<PcScanProgress> progress, CancellationToken ct) =>
        Task.Run(() => PcRepoFinder.FindAll(progress, ct)
            .Select(path => (Path: path, Remote: GitHubRemote.Find(path)))
            .Where(found => found.Remote != null)
            .Select(found => (found.Path, $"{found.Remote!.Value.Owner}/{found.Remote.Value.Name}"))
            .ToList(), ct);

    // git's own words, without the "fatal: " before them: "Remote branch x not found in upstream origin".
    private static string CloneError(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var errors = lines
            .Where(l => l.StartsWith("fatal:", StringComparison.Ordinal) || l.StartsWith("error:", StringComparison.Ordinal))
            .Select(l => l[(l.IndexOf(':') + 1)..].Trim())
            .ToList();
        return errors.Count > 0 ? string.Join(" ", errors) : lines.LastOrDefault() ?? "git clone failed.";
    }

    private static string CountOf(int repos) => repos == 1 ? "1 repo" : $"{repos} repos";

    private void Add(CloneRepoViewModel repo)
    {
        repo.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CloneRepoViewModel.IsOnPc) or nameof(CloneRepoViewModel.IsChecked) or nameof(CloneRepoViewModel.CanCheck))
                RaiseCounts();
        };
        Repos.Add(repo);
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(OnPcCount));
        OnPropertyChanged(nameof(NotOnPcCount));
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(AllShownChecked));
        CloneCommand.NotifyCanExecuteChanged();
    }

    private void RefreshView()
    {
        VisibleRepos.Refresh();
        OnPropertyChanged(nameof(AllShownChecked));
    }

    private bool IsShown(CloneRepoViewModel repo)
    {
        var inFilter = Filter switch
        {
            CloneFilter.NotOnPc => !repo.IsOnPc,
            CloneFilter.OnPc => repo.IsOnPc,
            _ => true,
        };
        var text = Search.Trim();
        return inFilter && (text.Length == 0 ||
                            repo.FullName.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                            (repo.Repo.Description?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false));
    }

    private IEnumerable<CloneRepoViewModel> Shown() => Repos.Where(IsShown);

    private string Summary() => $"{CountOf(Repos.Count)} on GitHub: {OnPcCount} on this PC, {NotOnPcCount} not.";

    private string StatusWhenIdle() => Account.IsSignedIn
        ? "Press Refresh to list your GitHub repos."
        : "Sign in to GitHub to list your repos.";

    private void OnSignInChanged()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        if (_gitHub.Login == _resultsAccount) return;

        // The list is the account's repos, so it doesn't carry over to another account (or to signed out).
        _load?.Cancel();
        _cloning?.Cancel();
        Repos.Clear();
        RaiseCounts();
        _resultsAccount = null;
        Status = StatusWhenIdle();
    }
}
