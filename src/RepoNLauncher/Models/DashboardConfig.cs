namespace RepoNLauncher.Models;

/// <summary>The whole persisted dashboard config, saved as a single JSON file.</summary>
public class DashboardConfig
{
    public List<string> ScanFolders { get; set; } = new();
    public List<ProjectInfo> Projects { get; set; } = new();

    /// <summary>Folder paths of the project-list sections the user collapsed; every other section starts expanded.</summary>
    public List<string> CollapsedGroups { get; set; } = new();

    public List<OpenWithApp> OpenWithApps { get; set; } = new();

    /// <summary>The folder Clone from GitHub last cloned into; null until it has.</summary>
    public string? CloneFolder { get; set; }

    /// <summary>The installed app has asked whether to add a desktop icon, or found one already, so it doesn't ask again.</summary>
    public bool DesktopIconAsked { get; set; }
}
