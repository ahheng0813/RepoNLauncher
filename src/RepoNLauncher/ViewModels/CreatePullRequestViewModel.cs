using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoNLauncher.Models;
using RepoNLauncher.Services;

namespace RepoNLauncher.ViewModels;

/// <summary>
/// The Create pull request dialog: the current branch into a branch picked on GitHub. When GitHub doesn't have the
/// branch, or some of its commits, it's pushed first.
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
    private string _suggestedTitle = string.Empty;
    private string _suggestedBody = string.Empty;
    private bool _needsPush;

    public CreatePullRequestViewModel(string path, string branch, (string Owner, string Name) repo, IGitService git, IGitHubService gitHub)
    {
        _path = path;
        _git = git;
        _gitHub = gitHub;
        Branch = branch;
        Owner = repo.Owner;
        RepoName = repo.Name;
    }

    public string Branch { get; }
    public string Owner { get; }
    public string RepoName { get; }
    public string RepoText => $"{Owner}/{RepoName}";

    /// <summary>Branches GitHub has, other than this one.</summary>
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
            var onGitHub = branches.Remote.Where(r => r.Remote == Remote).Select(r => r.Name).ToList();
            foreach (var name in onGitHub.Where(n => n != Branch).Order(StringComparer.OrdinalIgnoreCase)) BaseBranches.Add(name);

            if (!onGitHub.Contains(Branch))
            {
                _needsPush = true;
                PushNote = $"{Branch} isn't on GitHub yet. It's pushed there first.";
            }
            else if ((await _git.GetCommitSubjectsAsync(_path, $"{Remote}/{Branch}..HEAD", MaxCommitsListed)).Count is var unpushed and > 0)
            {
                _needsPush = true;
                PushNote = unpushed == 1
                    ? $"1 commit on {Branch} isn't on GitHub yet. It's pushed first."
                    : $"{unpushed} commits on {Branch} aren't on GitHub yet. They're pushed first.";
            }

            if (BaseBranches.Count == 0)
            {
                Error = $"GitHub has no other branch of {RepoText} to merge {Branch} into.";
                return;
            }

            var preferred = await _git.GetRemoteDefaultBranchAsync(_path, Remote);
            BaseBranch = BaseBranches.FirstOrDefault(b => b == preferred)
                         ?? UsualBaseBranches.FirstOrDefault(BaseBranches.Contains)
                         ?? BaseBranches[0];

            if (IsSignedIn)
            {
                try { Existing = await _gitHub.FindOpenPullRequestAsync(Owner, RepoName, Branch, CancellationToken.None); }
                catch (GitHubException) { } // creating it will say what's wrong
            }
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
    }

    partial void OnBaseBranchChanged(string? value)
    {
        if (value != null) _ = LoadCommitsAsync(value);
    }

    // The commits the pull request would bring in; they suggest the title and description until the user edits them.
    private async Task LoadCommitsAsync(string baseBranch)
    {
        var subjects = await _git.GetCommitSubjectsAsync(_path, $"{Remote}/{baseBranch}..HEAD", MaxCommitsListed + 1);
        if (baseBranch != BaseBranch) return; // another base was picked meanwhile

        CommitCount = subjects.Count;
        CommitsText = subjects.Count switch
        {
            0 => $"{baseBranch} already has every commit on {Branch}, so there's nothing to merge.",
            1 => $"1 commit to merge into {baseBranch}.",
            > MaxCommitsListed => $"More than {MaxCommitsListed} commits to merge into {baseBranch}.",
            _ => $"{subjects.Count} commits to merge into {baseBranch}.",
        };

        var title = subjects.Count == 1 ? subjects[0] : TitleFromBranch(Branch);
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

    private bool CanCreate() => !IsBusy && BaseBranch != null && Title.Trim().Length > 0 && CommitCount > 0 && Existing == null;

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

    private bool CanOpenOnGitHub() => !IsBusy && BaseBranch != null && CommitCount > 0;

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
