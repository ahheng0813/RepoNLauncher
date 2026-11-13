namespace RepoNLauncher.Models;

/// <summary>The whole persisted dashboard config, saved as a single JSON file.</summary>
public class DashboardConfig
{
    public List<string> ScanFolders { get; set; } = new();
    public List<ProjectInfo> Projects { get; set; } = new();

    /// <summary>Folder paths of the project-list sections the user collapsed; every other section starts expanded.</summary>
    public List<string> CollapsedGroups { get; set; } = new();

    public List<OpenWithApp> OpenWithApps { get; set; } = new();

    /// <summary>
    /// The screen the browser window opens a project's site on, such as \\.\DISPLAY2. Null puts it on another screen than
    /// the dashboard's, when there is one.
    /// </summary>
    public string? SiteScreen { get; set; }

    /// <summary>Sites open in the PC's default browser instead of the dashboard's browser window.</summary>
    public bool OpenSitesInDefaultBrowser { get; set; }

    /// <summary>The sites saved in the dashboard browser, in the order its bar shows them.</summary>
    public List<SavedSite> SavedSites { get; set; } = new();

    /// <summary>
    /// The dashboard browser keeps nothing on this PC (InPrivate): history, cookies, sign-ins, site storage and the cache
    /// are forgotten once every browser window is closed. Off, they're kept, so sites stay signed in.
    /// </summary>
    public bool BrowserIncognito { get; set; }

    /// <summary>Where the dashboard browser searches for what's typed in its address that isn't an address.</summary>
    public RepoNLauncher.Services.SearchEngine BrowserSearchEngine { get; set; } = RepoNLauncher.Services.SearchEngine.Google;

    /// <summary>The dashboard browser blocks ads, as strongly as <see cref="AdBlockLevel"/> says.</summary>
    public bool AdBlockEnabled { get; set; } = true;

    public RepoNLauncher.Services.AdBlockLevel AdBlockLevel { get; set; } = RepoNLauncher.Services.AdBlockLevel.Balanced;

    /// <summary>The folder Clone from GitHub last cloned into; null until it has.</summary>
    public string? CloneFolder { get; set; }

    /// <summary>The installed app has asked whether to add a desktop icon, or found one already, so it doesn't ask again.</summary>
    public bool DesktopIconAsked { get; set; }

    /// <summary>How dates and times are shown: Settings, Date &amp; time.</summary>
    public DateTimeFormatSettings DateTimeFormat { get; set; } = new();

    /// <summary>Dark, light, or as Windows' app mode says: Settings, General.</summary>
    public RepoNLauncher.Services.AppTheme Theme { get; set; } = RepoNLauncher.Services.AppTheme.Dark;
}
