namespace RepoNLauncher.Services;

/// <summary>Who makes the app and where it lives on GitHub, for Settings' About page and the updater.</summary>
public static class AppInfo
{
    public const string Name = "Repo'N'Launcher";
    public const string Description = "A Windows dashboard for the git repos on your PC: see their status, fetch, commit, and start them from one window.";

    public const string Author = "TEE KAI HENG";
    public const string AuthorLogin = "ahheng0813";
    public const string AuthorUrl = "https://github.com/" + AuthorLogin;

    public const string RepoUrl = "https://github.com/ahheng0813/RepoNLauncher";
    public const string ReleasesUrl = RepoUrl + "/releases";
    public const string IssuesUrl = RepoUrl + "/issues";

    public const string License = "Apache License 2.0";
    public const string LicenseUrl = RepoUrl + "/blob/main/LICENSE";

    public const string FormatCodesUrl = "https://learn.microsoft.com/dotnet/standard/base-types/custom-date-and-time-format-strings";
}
