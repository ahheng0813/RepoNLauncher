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
    private UpdateInfo? _ready;

    public UpdateViewModel(UpdateService updates)
    {
        _updates = updates;
        _timer.Tick += (_, _) => _ = CheckAsync();
    }

    public string VersionText => $"v{_updates.Version}";

    /// <summary>Only an installed copy can update itself.</summary>
    public bool CanUpdate => _updates.IsInstalled;

    [ObservableProperty] private string _status = string.Empty;

    /// <summary>A newer version, downloaded and ready: Restart to update applies it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    private string? _readyVersion;

    /// <summary>Raised by Restart to update; the window closes the app cleanly, and the updater takes over.</summary>
    public event Action? RestartRequested;

    public void Start()
    {
        if (!CanUpdate)
        {
            Status = "Updates come with the installed app";
            return;
        }
        _ = CheckAsync();
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
            _ready = update;
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
