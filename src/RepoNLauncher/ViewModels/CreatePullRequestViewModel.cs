using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoNLauncher.Models;
using RepoNLauncher.Services;

namespace RepoNLauncher.ViewModels;

/// <summary>
/// The Create pull request dialog: a branch (the current one at first) into a branch picked on GitHub. When GitHub
/// doesn't have the branch, or some of its commits, it's pushed first.
/// </summary>
public partial class CreatePullRequestViewModel : ObservableObject
{
    private const string Remote = "origin";
    private const int MaxCommitsListed = 50;

    // Picked first when the remote doesn't say which branch is its default.
    private static readonly string[] UsualBaseBranches = ["main", "master", "development", "develop"];

    private readonly string _path;
    private readonly IGitService _git;
    private readonly IGitHubService _gitHub;
    private readonly string _currentBranch;
    private string _suggestedTitle = string.Empty;
    private string _suggestedBody = string.Empty;
    private bool _needsPush;
    private List<string> _onGitHub = [];

    // Where the commits it brings in are counted from: the local branch when it's pushed first, else GitHub's copy,
    // which is what the pull request will compare.
    private string _headRef = string.Empty;
    private string? _gitHubDefault;
    private Task _headLoad = Task.CompletedTask;

    public CreatePullRequestViewModel(string path, string currentBranch, (string Owner, string Name) repo, IGitService git, IGitHubService gitHub)
    {
        _path = path;
        _git = git;
        _gitHub = gitHub;
        _currentBranch = currentBranch;
        Owner = repo.Owner;
        RepoName = repo.Name;
    }

    /// <summary>The From picker: the current branch, the other local ones, then ones only GitHub has.</summary>
    public ObservableCollection<BranchOption> HeadBranches { get; } = new();

    /// <summary>The branch the pull request is from.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand), nameof(OpenOnGitHubCommand))]
    private BranchOption? _headBranch;

    public string Branch => HeadBranch?.Name ?? string.Empty;

    public string Owner { get; }
    public string RepoName { get; }
    public string RepoText => $"{Owner}/{RepoName}";

    /// <summary>Branches GitHub has, other than the one it's from.</summary>
    public ObservableCollection<string> BaseBranches { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand), nameof(OpenOnGitHubCommand))]
    private string? _baseBranch;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private string _title = string.Empty;

    [ObservableProperty] private string _body = string.Empty;
    [ObservableProperty] private bool _isDraft;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand), nameof(OpenOnGitHubCommand))]
    private int _commitCount;

    [ObservableProperty] private string _commitsText = string.Empty;
    [ObservableProperty] private string? _pushNote;

    /// <summary>A pull request already open from this branch; GitHub allows one per branch and base.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private GitHubPullRequest? _existing;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand), nameof(OpenOnGitHubCommand))]
    private bool _isBusy;

    [ObservableProperty] private string? _busyText;
    [ObservableProperty] private string? _error;

    public bool IsSignedIn => _gitHub.Login != null;

    public string SignInNote => IsSignedIn ? string.Empty
        : "Creating it here asks you to sign in to GitHub first. Finish on GitHub needs no sign-in here.";

    /// <summary>The pull request made, once Create succeeds.</summary>
    public GitHubPullRequest? Created { get; private set; }

    /// <summary>Asks the user to sign in to GitHub; true once signed in.</summary>
    public Func<bool>? SignIn { get; set; }

    /// <summary>Opens a page in the browser.</summary>
    public Action<string>? OpenLink { get; set; }

    /// <summary>Raised when the dialog's work is done: the pull request was made, or GitHub took over in the browser.</summary>
    public event Action? Finished;

    public async Task LoadAsync()
    {
        IsBusy = true;
        BusyText = "Getting the latest branches from GitHub…";
        try
        {
            // Fetching first makes the branch list and the commit count match GitHub; offline, what git has will do.
            await _git.FetchAsync(_path);
            var branches = await _git.GetBranchesAsync(_path);
            _onGitHub = branches.Remote.Where(r => r.Remote == Remote).Select(r => r.Name).ToList();
            _gitHubDefault = await _git.GetRemoteDefaultBranchAsync(_path, Remote);

            if (branches.Local.Contains(_currentBranch)) HeadBranches.Add(new BranchOption(_currentBranch, null, isCurrent: true));
            foreach (var name in branches.Local.Where(n => n != _currentBranch).Order(StringComparer.OrdinalIgnoreCase))
                HeadBranches.Add(new BranchOption(name, null, isCurrent: false));
            foreach (var name in _onGitHub.Where(n => !branches.Local.Contains(n)).Order(StringComparer.OrdinalIgnoreCase))
                HeadBranches.Add(new BranchOption(name, Remote, isCurrent: false));

            if (HeadBranches.Count == 0)
            {
                Error = "This repo has no branch to make a pull request from yet.";
                return;
            }
            // A detached HEAD has no current branch in the list, so the first branch is picked instead.
            HeadBranch = HeadBranches[0];
            await _headLoad;
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
    }

    partial void OnHeadBranchChanged(BranchOption? value)
    {
        OnPropertyChanged(nameof(Branch));
        if (value != null) _headLoad = LoadHeadAsync(value);
    }

    // What depends on the branch it's from: the branches it can go into, what has to be pushed first, and whether a
    // pull request from it is open already. The base branch picked so far stays picked when it can.
    private async Task LoadHeadAsync(BranchOption head)
    {
        var keepBase = BaseBranch;
        Error = null;
        Existing = null;
        _needsPush = false;
        PushNote = null;
        // Set to null and back, so the commits are counted again even when the base branch stays the same.
        BaseBranch = null;
        CommitCount = 0;
        CommitsText = string.Empty;
        BaseBranches.Clear();
        foreach (var name in _onGitHub.Where(n => n != head.Name).Order(StringComparer.OrdinalIgnoreCase)) BaseBranches.Add(name);

        // One only GitHub has needs no push; a local one does when GitHub lacks it or some of its commits.
        string? pushNote = null;
        if (head.Remote == null && !_onGitHub.Contains(head.Name))
        {
            pushNote = $"{head.Name} isn't on GitHub yet. It's pushed there first.";
        }
        else if (head.Remote == null &&
                 (await _git.GetCommitSubjectsAsync(_path, $"{Remote}/{head.Name}..{head.Name}", MaxCommitsListed)).Count is var unpushed and > 0)
        {
            pushNote = unpushed == 1
                ? $"1 commit on {head.Name} isn't on GitHub yet. It's pushed first."
                : $"{unpushed} commits on {head.Name} aren't on GitHub yet. They're pushed first.";
        }
        if (head != HeadBranch) return; // another branch was picked meanwhile
        _needsPush = pushNote != null;
        PushNote = pushNote;
        _headRef = _needsPush ? head.Name : $"{Remote}/{head.Name}";

        if (BaseBranches.Count == 0)
        {
            Error = $"GitHub has no other branch of {RepoText} to merge {head.Name} into.";
            return;
        }
        BaseBranch = BaseBranches.FirstOrDefault(b => b == keepBase)
                     ?? BaseBranches.FirstOrDefault(b => b == _gitHubDefault)
                     ?? UsualBaseBranches.FirstOrDefault(BaseBranches.Contains)
                     ?? BaseBranches[0];

        if (!IsSignedIn) return;
        try
        {
            var existing = await _gitHub.FindOpenPullRequestAsync(Owner, RepoName, head.Name, CancellationToken.None);
            if (head == HeadBranch) Existing = existing;
        }
        catch (GitHubException) { } // creating it will say what's wrong
    }

    partial void OnBaseBranchChanged(string? value)
    {
        if (value != null && HeadBranch != null) _ = LoadCommitsAsync(HeadBranch, value);
    }

    // The commits the pull request would bring in; they suggest the title and description until the user edits them.
    private async Task LoadCommitsAsync(BranchOption head, string baseBranch)
    {
        var subjects = await _git.GetCommitSubjectsAsync(_path, $"{Remote}/{baseBranch}..{_headRef}", MaxCommitsListed + 1);
        if (head != HeadBranch || baseBranch != BaseBranch) return; // another branch was picked meanwhile

        CommitCount = subjects.Count;
        CommitsText = subjects.Count switch
        {
            0 => $"{baseBranch} already has every commit on {head.Name}, so there's nothing to merge.",
            1 => $"1 commit to merge into {baseBranch}.",
            > MaxCommitsListed => $"More than {MaxCommitsListed} commits to merge into {baseBranch}.",
            _ => $"{subjects.Count} commits to merge into {baseBranch}.",
        };

        var title = subjects.Count == 1 ? subjects[0] : TitleFromBranch(head.Name);
        // Oldest first reads as the story of the branch.
        var body = subjects.Count > 1 ? string.Join("\n", subjects.Take(MaxCommitsListed).Reverse().Select(s => $"- {s}")) : string.Empty;
        if (Title == _suggestedTitle) Title = title;
        if (Body == _suggestedBody) Body = body;
        _suggestedTitle = title;
        _suggestedBody = body;
    }

    // feature/login-page -> "Login page"; heng_fix_crash -> "Heng fix crash".
    private static string TitleFromBranch(string branch)
    {
        var words = branch[(branch.LastIndexOf('/') + 1)..].Replace('-', ' ').Replace('_', ' ').Trim();
        return words.Length == 0 ? branch : char.ToUpperInvariant(words[0]) + words[1..];
    }

    private bool CanCreate() =>
        !IsBusy && HeadBranch != null && BaseBranch != null && Title.Trim().Length > 0 && CommitCount > 0 && Existing == null;

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        Error = null;
        if (!IsSignedIn && SignIn?.Invoke() != true) return;
        OnPropertyChanged(nameof(SignInNote));

        IsBusy = true;
        try
        {
            if (!await PushIfNeededAsync()) return;

            BusyText = "Creating the pull request…";
            Created = await _gitHub.CreatePullRequestAsync(
                Owner, RepoName, Branch, BaseBranch!, Title.Trim(), Body.Trim(), IsDraft, CancellationToken.None);
            Finished?.Invoke();
        }
        catch (GitHubException ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
    }

    private bool CanOpenOnGitHub() => !IsBusy && HeadBranch != null && BaseBranch != null && CommitCount > 0;

    // GitHub's own page for a new pull request, filled in with the title and description; no sign-in here needed.
    [RelayCommand(CanExecute = nameof(CanOpenOnGitHub))]
    private async Task OpenOnGitHubAsync()
    {
        Error = null;
        IsBusy = true;
        try
        {
            if (!await PushIfNeededAsync()) return;

            var url = $"https://github.com/{Uri.EscapeDataString(Owner)}/{Uri.EscapeDataString(RepoName)}/compare/" +
                      $"{EscapeBranch(BaseBranch!)}...{EscapeBranch(Branch)}?expand=1" +
                      $"&title={Uri.EscapeDataString(Title.Trim())}&body={Uri.EscapeDataString(Body.Trim())}";
            OpenLink?.Invoke(url);
            Finished?.Invoke();
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
    }

    [RelayCommand]
    private void OpenExisting()
    {
        if (Existing != null) OpenLink?.Invoke(Existing.Url);
    }

    private async Task<bool> PushIfNeededAsync()
    {
        if (!_needsPush) return true;

        BusyText = $"Pushing {Branch} to GitHub…";
        var push = await _git.PushAsync(_path, Remote, Branch);
        if (!push.Success)
        {
            Error = $"Couldn't push {Branch} to GitHub: {push.Output}";
            return false;
        }
        _needsPush = false;
        PushNote = null;
        return true;
    }

    // Slashes stay: GitHub reads feature/login as one branch in a compare link.
    private static string EscapeBranch(string branch) => string.Join("/", branch.Split('/').Select(Uri.EscapeDataString));
}
