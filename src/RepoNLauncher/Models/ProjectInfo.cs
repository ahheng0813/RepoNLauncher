namespace RepoNLauncher.Models;

/// <summary>Persisted, user-editable record for a project the dashboard tracks.</summary>
public class ProjectInfo
{
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>The detected kind; <see cref="KindOverride"/> wins when the user picked one.</summary>
    public ProjectKind Kind { get; set; } = ProjectKind.Unknown;
    public ProjectKind? KindOverride { get; set; }
    public string? RunCommandOverride { get; set; }

    /// <summary>Start commands kept in the Run tab's list, each run with one click. Null when none were saved.</summary>
    public List<string>? SavedRunCommands { get; set; }
    /// <summary>A run opens the site it serves as soon as it prints the address; off, only the Run tab's button opens it.</summary>
    public bool OpenSiteOnRun { get; set; } = true;
    public string? CsprojOverride { get; set; }
    public string? EnvFileOverride { get; set; }
    public bool IsAutoDiscovered { get; set; }

    /// <summary>
    /// Per local branch, the branch Fetch Latest merges in after pulling: "development" merges origin/development.
    /// An empty value means only the branch itself is pulled; a branch not listed gets the default.
    /// </summary>
    public Dictionary<string, string>? MergeFrom { get; set; }
}
