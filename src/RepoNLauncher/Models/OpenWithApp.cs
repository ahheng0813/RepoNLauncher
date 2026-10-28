namespace RepoNLauncher.Models;

/// <summary>A user-added program in the Overview tab's OPEN IN row, shared by all projects.</summary>
public class OpenWithApp
{
    public const string PathPlaceholder = "{path}";
    public const string DefaultArguments = "\"" + PathPlaceholder + "\"";

    public string Name { get; set; } = string.Empty;

    /// <summary>Full path of the program, or a name Windows finds on its own (e.g. wt.exe).</summary>
    public string Program { get; set; } = string.Empty;

    /// <summary>Command-line arguments; <see cref="PathPlaceholder"/> is replaced with the project folder.</summary>
    public string Arguments { get; set; } = DefaultArguments;
}
