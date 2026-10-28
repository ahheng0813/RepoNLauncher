namespace RepoNLauncher.Models;

public class EnvEntry
{
    public string Source { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public bool IsHighlighted { get; set; }
}
