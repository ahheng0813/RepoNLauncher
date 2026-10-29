using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using RepoNLauncher.Services;

namespace RepoNLauncher.ViewModels;

public enum CloneState
{
    None,
    Waiting,
    Cloning,
    Done,
    Failed,
}

/// <summary>A branch in a repo's branch picker in Clone from GitHub.</summary>
public sealed class CloneBranchOption(string name, bool isDefault)
{
    public string Name { get; } = name;
    public bool IsDefault { get; } = isDefault;
    public string Note => IsDefault ? "  default" : string.Empty;
    public override string ToString() => Name;
}

/// <summary>A GitHub repo in Clone from GitHub: whether this PC has a clone of it, and cloning it.</summary>
public partial class CloneRepoViewModel : ObservableObject
{
    // "Receiving objects:  45% (450/1000), 1.2 MiB | 2.0 MiB/s" and "remote: Counting objects: 100% (12/12), done."
    private static readonly Regex ProgressLine = new(@"^(?:remote:\s*)?(?<what>[A-Za-z ]+):\s+(?<percent>\d+)%", RegexOptions.Compiled);

    private readonly IGitHubService _gitHub;
    private bool _branchesLoaded;

    public CloneRepoViewModel(GitHubRepoListing repo, IGitHubService gitHub)
    {
        Repo = repo;
        _gitHub = gitHub;
        var defaultBranch = new CloneBranchOption(repo.DefaultBranch, isDefault: true);
        Branches.Add(defaultBranch);
        _branch = defaultBranch;
    }

    public GitHubRepoListing Repo { get; }
    public string FullName => Repo.FullName;

    /// <summary>When it was last pushed to, such as "3 days ago".</summary>
    public string PushedText => Repo.PushedAt is { } pushed ? $"Pushed {Ago(pushed)}" : "Nothing pushed yet";

    /// <summary>The default branch until the picker is opened, which loads the rest.</summary>
    public ObservableCollection<CloneBranchOption> Branches { get; } = new();

    [ObservableProperty] private CloneBranchOption _branch;

    /// <summary>A clone of it on this PC; the dashboard's copy when it has one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnPc), nameof(CanCheck), nameof(LocalLabel), nameof(CanAddToDashboard))]
    private string? _localPath;

    /// <summary>The PC has been looked through, so a repo without <see cref="LocalPath"/> isn't on it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LocalLabel))]
    private bool _isPcChecked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddToDashboard))]
    private bool _isOnDashboard;

    [ObservableProperty] private bool _isChecked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheck), nameof(CanPickBranch))]
    private CloneState _cloneState;

    /// <summary>How the clone is going: Waiting…, Receiving objects 45%, the error.</summary>
    [ObservableProperty] private string? _cloneText;

    public bool IsOnPc => LocalPath != null;

    public bool CanCheck => !IsOnPc && CloneState is not (CloneState.Waiting or CloneState.Cloning);

    public bool CanPickBranch => CloneState is CloneState.None or CloneState.Failed;

    public bool CanAddToDashboard => IsOnPc && !IsOnDashboard;

    public string LocalLabel => IsOnPc ? "On this PC" : IsPcChecked ? "Not on this PC" : "Checking…";

    /// <summary>Fills the branch picker the first time it's opened. The pick so far stays picked.</summary>
    public async Task LoadBranchesAsync()
    {
        if (_branchesLoaded) return;
        _branchesLoaded = true;
        try
        {
            var names = await _gitHub.GetBranchNamesAsync(Repo.Owner, Repo.Name, CancellationToken.None);
            foreach (var name in names.Where(n => n != Repo.DefaultBranch).Order(StringComparer.OrdinalIgnoreCase))
                Branches.Add(new CloneBranchOption(name, isDefault: false));
        }
        catch (GitHubException ex)
        {
            _branchesLoaded = false; // try again the next time it's opened
            CloneText = $"Couldn't get the branches: {ex.Message}";
        }
    }

    public void ShowProgress(string line)
    {
        if (CloneState != CloneState.Cloning) return; // a report that arrived after the clone finished
        var match = ProgressLine.Match(line);
        if (match.Success) CloneText = $"{match.Groups["what"].Value.Trim()} {match.Groups["percent"].Value}%";
    }

    public void Cloned(string path)
    {
        CloneState = CloneState.Done;
        CloneText = Branch.IsDefault ? "Cloned." : $"Cloned, on {Branch.Name}.";
        LocalPath = path;
    }

    public void Failed(string message)
    {
        CloneState = CloneState.Failed;
        CloneText = message;
    }

    public void ClearClone()
    {
        CloneState = CloneState.None;
        CloneText = null;
    }

    private static string Ago(DateTimeOffset time)
    {
        var days = (int)(DateTimeOffset.Now - time).TotalDays;
        return days switch
        {
            < 1 => "today",
            1 => "yesterday",
            < 31 => $"{days} days ago",
            < 365 => days / 30 == 1 ? "a month ago" : $"{days / 30} months ago",
            _ => days / 365 == 1 ? "a year ago" : $"{days / 365} years ago",
        };
    }
}
