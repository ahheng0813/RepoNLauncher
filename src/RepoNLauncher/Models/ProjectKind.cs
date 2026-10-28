namespace RepoNLauncher.Models;

public enum ProjectKind
{
    Unknown,
    DotNet,
    Flutter,
    Node,
    // Saved to the config as a number, so new kinds go at the end.
    Python
}
