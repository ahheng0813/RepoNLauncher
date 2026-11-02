using System.IO;
using System.Reflection;
using Velopack;
using Velopack.Locators;
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

    /// <summary>Velopack's updater started this run: after applying an update, or after failing to.</summary>
    public static bool StartedByUpdater { get; set; }

    public bool IsInstalled => _manager.IsInstalled;

    /// <summary>Unzipped from the portable zip rather than installed; it still updates itself.</summary>
    public bool IsPortable => _manager.IsPortable;

    /// <summary>The installed version, or the build's own when not installed.</summary>
    public string Version =>
        _manager.CurrentVersion?.ToString() ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "?";

    /// <summary>
    /// The downloaded update the updater has just failed to apply, or null. When it can't, it starts the old version
    /// again and says nothing, so this run is that old version with the update still waiting.
    /// </summary>
    public VelopackAsset? FailedUpdate => StartedByUpdater ? _manager.UpdatePendingRestart : null;

    /// <summary>
    /// Installed for everyone on this PC (the .msi's choice, in Program Files): the updater asks Windows for an
    /// administrator's approval to update it.
    /// </summary>
    public bool NeedsAdminToUpdate
    {
        get
        {
            var root = VelopackLocator.IsCurrentSet ? VelopackLocator.Current.RootAppDir : null;
            if (root == null) return false;
            try
            {
                using var _ = new FileStream(Path.Combine(root, $".write-test-{Guid.NewGuid():N}"), FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
                return false;
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                return true;
            }
        }
    }

    /// <summary>The newest release when it's newer than this one; null when this is the newest.</summary>
    public Task<UpdateInfo?> CheckAsync() => _manager.CheckForUpdatesAsync();

    /// <summary>Downloads it in the background; it's applied on restart, or the next time the app starts.</summary>
    public Task DownloadAsync(UpdateInfo update, Action<int> progress) => _manager.DownloadUpdatesAsync(update, progress);

    /// <summary>Hands over to Velopack's updater, which waits for the app to close, applies the update and starts it again.</summary>
    public void ApplyOnExitAndRestart(VelopackAsset release) =>
        _manager.WaitExitThenApplyUpdates(release, silent: false, restart: true);
}
