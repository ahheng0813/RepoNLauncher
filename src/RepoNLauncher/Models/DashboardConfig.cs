namespace RepoNLauncher.Models;

/// <summary>The whole persisted dashboard config, saved as a single JSON file.</summary>
public class DashboardConfig
{
    public List<string> ScanFolders { get; set; } = new();
    public List<ProjectInfo> Projects { get; set; } = new();

    /// <summary>Folder paths of the project-list sections the user collapsed; every other section starts expanded.</summary>
    public List<string> CollapsedGroups { get; set; } = new();

    public List<OpenWithApp> OpenWithApps { get; set; } = new();
}
