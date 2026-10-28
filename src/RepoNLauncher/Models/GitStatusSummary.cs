namespace RepoNLauncher.Models;

public class GitStatusSummary
{
    public string Branch { get; set; } = string.Empty;
    public int Ahead { get; set; }
    public int Behind { get; set; }
    public bool IsDirty { get; set; }
}

public class GitPullResult
{
    public bool Success { get; set; }
    public string Output { get; set; } = string.Empty;
    public bool AlreadyUpToDate { get; set; }
}
