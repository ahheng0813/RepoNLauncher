namespace RepoNLauncher.Models;

public class CommitInfo
{
    public string Hash { get; set; } = string.Empty;
    public string ShortHash => Hash.Length >= 7 ? Hash[..7] : Hash;

    /// <summary>The first parent, which the commit's changes are shown against; null for the repo's first commit.</summary>
    public string? ParentHash { get; set; }
    public string Author { get; set; } = string.Empty;
    public DateTimeOffset Date { get; set; }
    public string Message { get; set; } = string.Empty;

    /// <summary>The lines under the message's first one; empty when there are none.</summary>
    public string Body { get; set; } = string.Empty;
}
