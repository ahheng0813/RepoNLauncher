namespace RepoNLauncher.Models;

/// <summary>A site saved in the dashboard browser: in the bar under its address in every browser window, and its start page.</summary>
public class SavedSite
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}
