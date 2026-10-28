using System.IO;
using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace RepoNLauncher.Services;

/// <summary>
/// Updates the app from the GitHub releases release.ps1 publishes. Only an installed copy (from the Setup.exe or the
/// .msi) updates; one run from source or from the publish folder doesn't. Setting REPONLAUNCHER_UPDATE_FEED to a folder
/// of releases (release.ps1's release\out) updates from there instead, to try an update before publishing it.
/// </summary>
public class UpdateService
{
    public const string RepoUrl = "https://github.com/ahheng0813/RepoNLauncher";
    private const string FeedVariable = "REPONLAUNCHER_UPDATE_FEED";

    private readonly UpdateManager _manager;

    public UpdateService()
    {
        var feed = Environment.GetEnvironmentVariable(FeedVariable);
        IUpdateSource source = !string.IsNullOrWhiteSpace(feed) && Directory.Exists(feed)
            ? new SimpleFileSource(new DirectoryInfo(feed))
            : new GithubSource(RepoUrl, accessToken: null, prerelease: false);
        _manager = new UpdateManager(source);
    }

    public bool IsInstalled => _manager.IsInstalled;

    /// <summary>Unzipped from the portable zip rather than installed; it still updates itself.</summary>
    public bool IsPortable => _manager.IsPortable;

    /// <summary>The installed version, or the build's own when not installed.</summary>
    public string Version =>
        _manager.CurrentVersion?.ToString() ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "?";

    /// <summary>The newest release when it's newer than this one; null when this is the newest.</summary>
    public Task<UpdateInfo?> CheckAsync() => _manager.CheckForUpdatesAsync();

    /// <summary>Downloads it in the background; it's applied on restart, or the next time the app starts.</summary>
    public Task DownloadAsync(UpdateInfo update, Action<int> progress) => _manager.DownloadUpdatesAsync(update, progress);

    /// <summary>Hands over to Velopack's updater, which waits for the app to close, applies the update and starts it again.</summary>
    public void ApplyOnExitAndRestart(UpdateInfo update) =>
        _manager.WaitExitThenApplyUpdates(update.TargetFullRelease, silent: false, restart: true);
}
