namespace RepoNLauncher.Models;

public enum FileChangeType
{
    Modified,
    Added,
    Deleted,
    Renamed,
    Copied,
    Untracked,
    Conflicted,
}

public class FileChangeInfo
{
    public string Path { get; set; } = string.Empty;
    public string? OriginalPath { get; set; }
    public FileChangeType Type { get; set; }
    public bool IsStaged { get; set; }

    /// <summary>In the Commits tab: its path, or a line the commit added or removed in it, has the searched text.</summary>
    public bool IsSearchMatch { get; set; }

    public string DisplayPath => OriginalPath != null ? $"{OriginalPath} -> {Path}" : Path;
}
