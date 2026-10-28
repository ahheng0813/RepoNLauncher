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
    public string? CsprojOverride { get; set; }
    public string? EnvFileOverride { get; set; }
    public bool IsAutoDiscovered { get; set; }
}
