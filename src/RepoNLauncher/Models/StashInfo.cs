namespace RepoNLauncher.Models;

public class StashInfo
{
    public string Reference { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTimeOffset Date { get; set; }
}

public enum ConflictResolution { Ours, Theirs }
