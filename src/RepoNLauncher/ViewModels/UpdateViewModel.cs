using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoNLauncher.Services;
using Velopack;

namespace RepoNLauncher.ViewModels;

/// <summary>
/// The version and update state in the status bar. The installed app checks for a newer release when it starts and
/// every few hours, downloads it in the background, then offers Restart to update.
/// </summary>
public partial class UpdateViewModel : ObservableObject
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(4);

    private readonly UpdateService _updates;
    private readonly DispatcherTimer _timer = new() { Interval = CheckEvery };
    private VelopackAsset? _ready;

    public UpdateViewModel(UpdateService updates)
    {
        _updates = updates;
        _timer.Tick += (_, _) => _ = CheckAsync();
    }

    public string VersionText => $"v{_updates.Version}";

    /// <summary>Only an installed copy can update itself.</summary>
    public bool CanUpdate => _updates.IsInstalled;

    /// <summary>An installed copy, or one unzipped from the portable zip, can remove itself and everything it keeps.</summary>
    public bool CanUninstall => _updates.IsInstalled;

    /// <summary>Installed by the Setup.exe or the .msi, not unzipped from the portable zip.</summary>
    public bool IsInstalledBySetup => _updates.IsInstalled && !_updates.IsPortable;

    [ObservableProperty] private string _status = string.Empty;

    /// <summary>A newer version, downloaded and ready: Restart to update applies it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    private string? _readyVersion;

    /// <summary>Raised by Restart to update; the window closes the app cleanly, and the updater takes over.</summary>
    public event Action? RestartRequested;

    /// <summary>Why the update the updater just tried wasn't installed, for the window to tell; null when none failed.</summary>
    public string? InstallFailure { get; private set; }

    public void Start()
    {
        if (!CanUpdate)
        {
            Status = "Updates come with the installed app";
            return;
        }

        if (_updates.FailedUpdate is { } failed)
        {
            // The updater couldn't apply it and started this version again, without saying so. It's still downloaded,
            // so Restart to update can try again once whatever stopped it is out of the way.
            _ready = failed;
            ReadyVersion = failed.Version.ToString();
            Status = $"Version {ReadyVersion} wasn't installed";
            InstallFailure = _updates.NeedsAdminToUpdate
                ? $"Version {ReadyVersion} wasn't installed, so this is still version {_updates.Version}.\n\n" +
                  "Repo'N'Launcher is installed for everyone on this PC, so Windows asks for an administrator's approval to update it. " +
                  "Click Restart to update and choose Yes when Windows asks.\n\n" +
                  "If Windows didn't ask, a program was using Repo'N'Launcher's folder: close it, or restart Windows, and try again."
                : $"Version {ReadyVersion} wasn't installed, so this is still version {_updates.Version}: another program was using " +
                  "Repo'N'Launcher's folder, such as a console open in it.\n\n" +
                  "Close that program, or restart Windows, then click Restart to update.";
        }
        else
        {
            _ = CheckAsync();
        }
        _timer.Start();
    }

    private bool CanCheck() => CanUpdate && ReadyVersion == null;

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckAsync()
    {
        if (!CanCheck()) return;
        Status = "Checking for updates…";
        try
        {
            if (await _updates.CheckAsync() is not { } update)
            {
                Status = $"Up to date · checked {DateTime.Now:t}";
                return;
            }

            var version = update.TargetFullRelease.Version.ToString();
            var dispatcher = Dispatcher.CurrentDispatcher;
            Status = $"Downloading {version}…";
            await _updates.DownloadAsync(update, percent => dispatcher.InvokeAsync(() => Status = $"Downloading {version}… {percent}%"));
            _ready = update.TargetFullRelease;
            ReadyVersion = version;
            Status = $"Version {version} is ready";
        }
        catch (Exception ex)
        {
            // Offline, GitHub's limit for unsigned-in checks, or no public release yet: try again at the next check.
            System.Diagnostics.Debug.WriteLine($"Update check failed: {ex}");
            Status = "Couldn't check for updates";
        }
    }

    [RelayCommand]
    private void Restart() => RestartRequested?.Invoke();

    /// <summary>Starts the updater; it applies the update once the app has closed, then starts it again.</summary>
    public void ApplyOnExit()
    {
        if (_ready != null) _updates.ApplyOnExitAndRestart(_ready);
    }
}
