namespace RepoNLauncher.Models;

public class CommitInfo
{
    public string Hash { get; set; } = string.Empty;
    public string ShortHash => Hash.Length >= 7 ? Hash[..7] : Hash;
    public string Author { get; set; } = string.Empty;
    public DateTimeOffset Date { get; set; }
    public string Message { get; set; } = string.Empty;
}
