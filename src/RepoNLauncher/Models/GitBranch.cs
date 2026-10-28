namespace RepoNLauncher.Models;

/// <summary>A branch on a remote, as git last fetched it: origin/feature is ("origin", "feature").</summary>
public record RemoteBranch(string Remote, string Name);

/// <summary>A repo's branches: its own, and the remotes' as of the last fetch.</summary>
public record GitBranches(IReadOnlyList<string> Local, IReadOnlyList<RemoteBranch> Remote);

/// <summary>A pull request on GitHub.</summary>
public record GitHubPullRequest(int Number, string Title, string Url, bool IsDraft);
