using CommunityToolkit.Mvvm.ComponentModel;
using RepoNLauncher.Services;

namespace RepoNLauncher.ViewModels;

public enum RepoOwnership
{
    Checking,
    Mine,
    Others,
    NotOnGitHub,
    /// <summary>GitHub won't show it to this account: private to someone else, or deleted.</summary>
    NoAccess,
    /// <summary>The check stopped (cancelled, offline, rate limit) before reaching this repo.</summary>
    NotChecked,
}

/// <summary>A git repo found on the PC, with who owns it on GitHub.</summary>
public partial class PcRepoViewModel : ObservableObject
{
    public PcRepoViewModel(string path, (string Owner, string Name)? remote)
    {
        Path = path;
        var name = System.IO.Path.GetFileName(path);
        Name = name.Length > 0 ? name : path;
        Remote = remote;
        _gitHubName = remote is { } r ? $"{r.Owner}/{r.Name}" : null;
        _ownership = remote == null ? RepoOwnership.NotOnGitHub : RepoOwnership.Checking;
    }

    public string Path { get; }
    public string Name { get; }

    /// <summary>The GitHub repo the clone's remote names, before asking GitHub.</summary>
    public (string Owner, string Name)? Remote { get; }

    /// <summary>"owner/name" from the remote at first, then as GitHub reports it (after a rename or transfer).</summary>
    [ObservableProperty] private string? _gitHubName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OwnershipLabel))]
    private RepoOwnership _ownership;

    [ObservableProperty] private bool _isPrivate;
    [ObservableProperty] private bool _isOnDashboard;
    [ObservableProperty] private bool _isChecked;

    public string OwnershipLabel => Ownership switch
    {
        RepoOwnership.Mine => "Mine",
        RepoOwnership.Others => "Others",
        RepoOwnership.NotOnGitHub => "Not on GitHub",
        RepoOwnership.NoAccess => "No access",
        RepoOwnership.NotChecked => "Not checked",
        _ => "Checking…",
    };

    public void ApplyGitHubAnswer(GitHubRepo? repo, string login)
    {
        if (repo == null)
        {
            Ownership = RepoOwnership.NoAccess;
            return;
        }

        GitHubName = repo.FullName;
        IsPrivate = repo.IsPrivate;
        Ownership = repo.Owner.Equals(login, StringComparison.OrdinalIgnoreCase) ? RepoOwnership.Mine : RepoOwnership.Others;
    }
}
