using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoNLauncher.Models;
using RepoNLauncher.Services;

namespace RepoNLauncher.ViewModels;

// The picker choices are classes, not records, on purpose: switching projects swaps the pickers' lists, and a picker
// keeps a selection that equals an item of the new list. With value equality, a branch picked in one project would
// show up picked in the next one.

/// <summary>A branch in the Overview's branch picker; <see cref="Remote"/> is set for one only the remote has so far.</summary>
public sealed class BranchOption(string name, string? remote, bool isCurrent)
{
    public string Name { get; } = name;
    public string? Remote { get; } = remote;
    public bool IsCurrent { get; } = isCurrent;
    public string Note => IsCurrent ? "  current" : Remote == null ? string.Empty : $"  on {Remote}";

    /// <summary>What git calls it: main, or origin/main for the remote's.</summary>
    public string Ref => Remote == null ? Name : $"{Remote}/{Name}";
    public override string ToString() => Name;
}

/// <summary>A choice in the Overview's Merge from picker; a null <see cref="Branch"/> means just pull the branch itself.</summary>
public sealed class MergeSourceOption(string? branch, string label)
{
    public string? Branch { get; } = branch;
    public string Label { get; } = label;
    public override string ToString() => Label;
}

public partial class ProjectViewModel : ObservableObject
{
    // heng_main is a personal branch some repos use that should always track development, so until another choice
    // is made for it, Fetch Latest merges development into it.
    private const string AutoMergeOnBranch = "heng_main";
    private const string AutoMergeSourceBranch = "development";

    // Dev servers (Vite, Next, Express, Django, Flask, ...) print the address they serve on; the first local URL
    // in a line becomes a button. Color codes are stripped first since some tools color the URL.
    private static readonly Regex LocalUrlPattern = new(
        @"https?://(?:localhost|127\.0\.0\.1|0\.0\.0\.0|\[::1?\])(?::\d+)?[^\s""'<>]*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AnsiEscape = new(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);

    public static IReadOnlyList<ProjectKind> AllKinds { get; } = Enum.GetValues<ProjectKind>();

    private readonly IGitService _git;
    private readonly IFlutterService _flutter;
    private readonly IDotNetService _dotNet;
    private readonly IEnvironmentParser _envParser;
    private readonly IProcessRunner _processRunner;
    private readonly IPortService _ports;
    private readonly IIdeService _ides;

    public ProjectInfo Info { get; }

    public string Name => Info.Name;
    public string Path => Info.Path;

    /// <summary>What the dashboard treats the project as: the kind picked in the Overview tab, else the detected one.</summary>
    public ProjectKind Kind
    {
        get => Info.KindOverride ?? Info.Kind;
        set
        {
            if (value == Kind) return;

            // Picking the detected kind again drops the override, so the project goes back to following detection.
            Info.KindOverride = value == Info.Kind ? null : value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DetectedKindNote));
            OnPropertyChanged(nameof(StartCommandHint));
            OnPropertyChanged(nameof(CanChooseStartFile));
            SettingsChanged?.Invoke();

            _ = LoadEnvironmentAsync();
            if (value == ProjectKind.Flutter) _ = LoadDevicesAsync();
        }
    }

    public string DetectedKindNote => Info.KindOverride == null ? string.Empty : $"detected as {Info.Kind}";

    /// <summary>The command typed in the Run tab; while it's empty, Run uses the kind's own (flutter run / dotnet run).</summary>
    public string StartCommand
    {
        get => Info.RunCommandOverride ?? string.Empty;
        set
        {
            // No change notification: the text box is the only writer, and echoing a cleaned-up value back would fight the typing.
            Info.RunCommandOverride = string.IsNullOrWhiteSpace(value) ? null : value;
            SettingsChanged?.Invoke();
            SaveStartCommandCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Start commands kept for this project, each with its own run button. The box's command stays separate.</summary>
    public ObservableCollection<string> SavedCommands { get; }

    /// <summary>Placeholder for the start command box: what Run does while it's empty, or an example.</summary>
    public string StartCommandHint => Kind switch
    {
        ProjectKind.Flutter => "Leave empty to use flutter run on the selected device",
        ProjectKind.DotNet => "Leave empty to use dotnet run on the project's .csproj",
        ProjectKind.Node => "e.g. npm run dev, or choose the file to run",
        ProjectKind.Python => "e.g. python main.py, or choose the file to run",
        _ => "The command that starts this project, or choose the file to run",
    };

    /// <summary>Flutter and .NET start from their project files; the other kinds often start from one script.</summary>
    public bool CanChooseStartFile => Kind is not (ProjectKind.Flutter or ProjectKind.DotNet);

    /// <summary>Raised when a setting kept in the config (kind, start command) changes, so it gets saved.</summary>
    public event Action? SettingsChanged;

    [ObservableProperty] private bool _isChecked;

    /// <summary>Mirrors MainViewModel.SelectedProject; the project list highlights from this rather than its own
    /// selection, which collapsing a section moves onto the section.</summary>
    [ObservableProperty] private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DefaultMergeName), nameof(MergeFromHint), nameof(IsMergePickUnsaved), nameof(NeedsPublish))]
    [NotifyCanExecuteChangedFor(nameof(SwitchBranchCommand), nameof(UpdateMergeDefaultCommand), nameof(PublishBranchCommand))]
    private string _branch = string.Empty;

    /// <summary>The current branch has a branch on a remote that it pulls from and pushes to.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsPublish))]
    [NotifyCanExecuteChangedFor(nameof(PublishBranchCommand))]
    private bool _isPublished = true;

    /// <summary>Where Publish branch pushes to: origin, else the repo's first remote; null when it has none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsPublish), nameof(NotPublishedText))]
    [NotifyCanExecuteChangedFor(nameof(PublishBranchCommand))]
    private string? _publishRemote;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private int _ahead;
    [ObservableProperty] private int _behind;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConsoleInputHint))]
    [NotifyCanExecuteChangedFor(nameof(SendInputCommand), nameof(SendKeyCommand), nameof(RunSavedCommand))]
    private bool _isRunning;
    [ObservableProperty] private DeviceInfo? _selectedDevice;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelectedFileConflicted))]
    private FileChangeInfo? _selectedChangedFile;

    [ObservableProperty] private bool _hasMergeConflict;
    [ObservableProperty] private bool _isWorkingTreeClean;
    [ObservableProperty] private string _stashMessage = string.Empty;
    [ObservableProperty] private string _commitMessage = string.Empty;

    public bool IsSelectedFileConflicted => SelectedChangedFile?.Type == FileChangeType.Conflicted;

    public ObservableCollection<CommitInfo> Commits { get; } = new();
    public ObservableCollection<DeviceInfo> Devices { get; } = new();
    public ObservableCollection<string> ConsoleOutput { get; } = new();
    public ObservableCollection<string> DetectedUrls { get; } = new();
    public ObservableCollection<EnvEntry> EnvEntries { get; } = new();
    public ObservableCollection<FileChangeInfo> ChangedFiles { get; } = new();
    public ObservableCollection<StashInfo> Stashes { get; } = new();
    public ObservableCollection<string> DiffLines { get; } = new();

    /// <summary>The Switch to picker: local branches, then ones only the remote has (switching to one makes a local copy).</summary>
    public ObservableCollection<BranchOption> Branches { get; } = new();

    /// <summary>The Merge from picker: just this branch, or another branch to merge in.</summary>
    public ObservableCollection<MergeSourceOption> MergeSources { get; } = new();

    // Picking only chooses; Switch, Update default and Merge now act on the choice.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SwitchBranchCommand))]
    private BranchOption? _selectedBranch;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMergePickUnsaved))]
    [NotifyCanExecuteChangedFor(nameof(UpdateMergeDefaultCommand), nameof(MergeNowCommand))]
    private MergeSourceOption? _selectedMergeSource;

    /// <summary>The branch Fetch Latest merges into this one by default, as shown: "none" when it just pulls.</summary>
    public string DefaultMergeName => MergeSourceFor(Branch) ?? "none";

    public string MergeFromHint => MergeSourceFor(Branch) is { } source
        ? $"Fetch Latest pulls {Branch}, then merges {source} into it."
        : $"Fetch Latest just pulls {Branch}.";

    /// <summary>The current branch was made here and isn't on the remote yet, so Publish branch is offered.</summary>
    public bool NeedsPublish => !IsPublished && PublishRemote != null && Branch is not ("" or "HEAD");

    public string NotPublishedText => $"not on {PublishRemote} yet";

    /// <summary>A branch other than the default is picked and not saved.</summary>
    public bool IsMergePickUnsaved => SelectedMergeSource != null && SelectedMergeSource.Branch != MergeSourceFor(Branch);

    private RunningProcessHandle? _runningProcess;

    // The console's last line is the start of one still being written, such as a prompt waiting for an answer.
    private bool _lastLineIsPartial;

    /// <summary>The Run tab's input box: what's typed here goes to the running app when Enter is pressed.</summary>
    [ObservableProperty] private string _consoleInput = string.Empty;

    public string ConsoleInputHint => IsRunning
        ? "Type here and press Enter to send it to the app, e.g. an answer to its prompt"
        : "Input for the app, once it's running";

    /// <summary>Raised when a Flutter app is launched on a device, so the dashboard can show that device's screen.</summary>
    public event Action<DeviceInfo>? RunStartedOnDevice;

    public ProjectViewModel(
        ProjectInfo info,
        IGitService git,
        IFlutterService flutter,
        IDotNetService dotNet,
        IEnvironmentParser envParser,
        IProcessRunner processRunner,
        IPortService ports,
        IIdeService ides)
    {
        Info = info;
        _git = git;
        _flutter = flutter;
        _dotNet = dotNet;
        _envParser = envParser;
        _processRunner = processRunner;
        _ports = ports;
        _ides = ides;
        SavedCommands = new ObservableCollection<string>(info.SavedRunCommands ?? new List<string>());
    }

    [RelayCommand]
    public async Task RefreshStatusAsync()
    {
        if (!_git.IsGitRepo(Path)) return;

        IsBusy = true;
        try
        {
            var status = await _git.GetStatusAsync(Path);
            Branch = status.Branch;
            IsDirty = status.IsDirty;
            Ahead = status.Ahead;
            Behind = status.Behind;
            IsPublished = status.HasUpstream;
            // Only the project on screen shows the pickers; Fetch All refreshes every project's status.
            if (IsSelected)
            {
                PublishRemote = await _git.GetPublishRemoteAsync(Path);
                await LoadBranchesAsync();
                // A remote may have been added or changed since.
                OnPropertyChanged(nameof(GitHubToolTip));
                OpenInGitHubCommand.NotifyCanExecuteChanged();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadBranchesAsync()
    {
        var branches = await _git.GetBranchesAsync(Path);
        var current = Branch;

        // No awaits from here on: two refreshes at once each rebuild the lists whole. Both pickers go back to what's in
        // effect: the current branch, and its default branch to merge from.
        Branches.Clear();
        foreach (var name in branches.Local.Order(StringComparer.OrdinalIgnoreCase)) Branches.Add(new BranchOption(name, null, name == current));
        var remoteOnly = branches.Remote
            .Where(r => !branches.Local.Contains(r.Name))
            .OrderBy(r => r.Remote == "origin" ? 0 : 1)
            .DistinctBy(r => r.Name)
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var branch in remoteOnly) Branches.Add(new BranchOption(branch.Name, branch.Remote, isCurrent: false));
        // A detached HEAD (or a repo with no commits yet) has no branch in the list; show what git calls it.
        if (!Branches.Any(b => b.IsCurrent)) Branches.Insert(0, new BranchOption(current, null, isCurrent: true));
        SelectedBranch = Branches.First(b => b.IsCurrent);

        MergeSources.Clear();
        MergeSources.Add(new MergeSourceOption(null, "Same branch (just pull it)"));
        var source = MergeSourceFor(current);
        var others = branches.Local.Concat(branches.Remote.Select(r => r.Name))
            .Append(source)
            .OfType<string>()
            .Where(name => name != current)
            .Distinct()
            .Order(StringComparer.OrdinalIgnoreCase);
        foreach (var name in others) MergeSources.Add(new MergeSourceOption(name, name));
        SelectedMergeSource = MergeSources.FirstOrDefault(m => m.Branch == source) ?? MergeSources[0];
    }

    private bool CanUpdateMergeDefault() => Branch.Length > 0 && IsMergePickUnsaved;

    /// <summary>Saves the picked branch as the one Fetch Latest merges into the current branch, from now on.</summary>
    [RelayCommand(CanExecute = nameof(CanUpdateMergeDefault))]
    private void UpdateMergeDefault()
    {
        var source = SelectedMergeSource!.Branch;

        // The built-in default needs no entry, which keeps heng_main following development unless told otherwise.
        Info.MergeFrom ??= new Dictionary<string, string>();
        if (source == DefaultMergeSource(Branch)) Info.MergeFrom.Remove(Branch);
        else Info.MergeFrom[Branch] = source ?? string.Empty;
        if (Info.MergeFrom.Count == 0) Info.MergeFrom = null;
        SettingsChanged?.Invoke();

        OnPropertyChanged(nameof(DefaultMergeName));
        OnPropertyChanged(nameof(MergeFromHint));
        OnPropertyChanged(nameof(IsMergePickUnsaved));
        UpdateMergeDefaultCommand.NotifyCanExecuteChanged();
        StatusMessage = source != null
            ? $"Default saved: Fetch Latest now merges {source} into {Branch} after pulling it."
            : $"Default saved: Fetch Latest now just pulls {Branch}.";
    }

    private bool CanMergeNow() => SelectedMergeSource?.Branch != null;

    /// <summary>Merges the picked branch into the current one this once. The default stays as it is.</summary>
    [RelayCommand(CanExecute = nameof(CanMergeNow))]
    private async Task MergeNowAsync()
    {
        var source = SelectedMergeSource!.Branch!;
        var branch = Branch;
        IsBusy = true;
        StatusMessage = $"Merging {source} into {branch}...";
        try
        {
            // Get the branch's latest first; offline, what git already has is merged.
            await _git.FetchAsync(Path);
            StatusMessage = await MergeInAsync(branch, source);
            await RefreshStatusAsync();
            await LoadChangesAsync();
            await LoadCommitsAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Merges the remote's copy of source when there is one, else the local branch; says how it went.
    private async Task<string> MergeInAsync(string branch, string source)
    {
        var mergeRef = await _git.MergeRefForAsync(Path, source);
        var merge = await _git.MergeAsync(Path, mergeRef);
        if (merge.Success) return merge.AlreadyUpToDate ? $"{mergeRef} already merged." : $"Merged {mergeRef} into {branch}.";
        return _git.HasMergeConflict(Path)
            ? $"Merging {mergeRef} hit conflicts: resolve them in the Changes tab."
            : $"Merging {mergeRef} failed: {merge.Output}";
    }

    /// <summary>The branch Fetch Latest merges into <paramref name="branch"/> after pulling it, or null for none.</summary>
    private string? MergeSourceFor(string branch) =>
        Info.MergeFrom != null && Info.MergeFrom.TryGetValue(branch, out var source)
            ? (source.Length == 0 ? null : source)
            : DefaultMergeSource(branch);

    private static string? DefaultMergeSource(string branch) =>
        string.Equals(branch, AutoMergeOnBranch, StringComparison.OrdinalIgnoreCase) ? AutoMergeSourceBranch : null;

    private bool CanSwitchBranch() => SelectedBranch is { IsCurrent: false };

    [RelayCommand(CanExecute = nameof(CanSwitchBranch))]
    private async Task SwitchBranchAsync()
    {
        var target = SelectedBranch!;
        IsBusy = true;
        StatusMessage = $"Switching to {target.Name}...";
        try
        {
            var result = await _git.SwitchBranchAsync(Path, target.Name, target.Remote);
            await RefreshStatusAsync();
            if (result.Success)
                StatusMessage = IsDirty ? $"Switched to {target.Name}. Your uncommitted changes came along." : $"Switched to {target.Name}.";
            else if (result.Output.Contains("would be overwritten", StringComparison.OrdinalIgnoreCase))
                StatusMessage = $"Couldn't switch to {target.Name}: it would overwrite uncommitted changes. Commit or stash them first (Changes tab).";
            else
                StatusMessage = $"Couldn't switch to {target.Name}: {result.Output}";

            await LoadChangesAsync();
            await LoadCommitsAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Pushes a branch made here to the remote for the first time, and has it pull from and push to that copy from
    /// then on, so Fetch Latest pulls it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(NeedsPublish))]
    private async Task PublishBranchAsync()
    {
        var branch = Branch;
        var remote = PublishRemote!;
        IsBusy = true;
        StatusMessage = $"Publishing {branch} to {remote}...";
        try
        {
            var result = await _git.PushAsync(Path, remote, branch);
            await RefreshStatusAsync();
            StatusMessage = result.Success
                ? $"Published {branch} to {remote}. Fetch Latest now pulls it from there."
                : $"Couldn't publish {branch}: {result.Output}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task FetchLatestAsync()
    {
        if (!_git.IsGitRepo(Path))
        {
            StatusMessage = "Not a git repository.";
            return;
        }

        IsBusy = true;
        StatusMessage = "Pulling...";
        try
        {
            var branch = await _git.GetCurrentBranchAsync(Path);
            string message;
            bool pulled;
            if (await _git.GetUpstreamAsync(Path) != null)
            {
                var result = await _git.PullAsync(Path);
                pulled = result.Success;
                message = result.Success
                    ? (result.AlreadyUpToDate ? "Already up to date." : "Pulled latest changes.")
                    : $"Pull failed: {result.Output}";
            }
            else
            {
                // A branch made here and never pushed has nothing to pull, but a branch to merge in may still be new.
                var result = await _git.FetchAsync(Path);
                pulled = result.Success;
                message = result.Success ? $"Fetched. {branch} isn't on the remote, so there was nothing to pull." : $"Fetch failed: {result.Output}";
            }

            if (pulled && MergeSourceFor(branch) is { } source) message += " " + await MergeInAsync(branch, source);

            StatusMessage = message;
            await RefreshStatusAsync();
            await LoadChangesAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task FetchLatestAndRefreshCommitsAsync()
    {
        await FetchLatestAsync();
        await LoadCommitsAsync();
    }

    [RelayCommand]
    public async Task LoadCommitsAsync()
    {
        if (!_git.IsGitRepo(Path)) return;

        IsBusy = true;
        try
        {
            var commits = await _git.GetRecentCommitsAsync(Path, 30);
            Commits.Clear();
            foreach (var c in commits) Commits.Add(c);
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSelectedChangedFileChanged(FileChangeInfo? value)
    {
        DiffLines.Clear();
        if (value == null) return;
        _ = LoadDiffAsync(value);
    }

    private async Task LoadDiffAsync(FileChangeInfo file)
    {
        var diff = await _git.GetFileDiffAsync(Path, file);
        if (SelectedChangedFile != file) return; // a newer selection's diff is already loading

        DiffLines.Clear();
        foreach (var line in diff.Replace("\r\n", "\n").Split('\n')) DiffLines.Add(line);
    }

    private void Report(GitPullResult result, string successMessage, string failurePrefix) =>
        StatusMessage = result.Success ? successMessage : $"{failurePrefix}: {result.Output}";

    [RelayCommand]
    public async Task LoadChangesAsync()
    {
        if (!_git.IsGitRepo(Path)) return;

        IsBusy = true;
        try
        {
            HasMergeConflict = _git.HasMergeConflict(Path);

            var changes = await _git.GetChangedFilesAsync(Path);
            var previouslySelected = SelectedChangedFile?.Path;
            ChangedFiles.Clear();
            foreach (var c in changes) ChangedFiles.Add(c);
            SelectedChangedFile = ChangedFiles.FirstOrDefault(c => c.Path == previouslySelected) ?? ChangedFiles.FirstOrDefault();
            IsWorkingTreeClean = ChangedFiles.Count == 0;

            var stashes = await _git.GetStashListAsync(Path);
            Stashes.Clear();
            foreach (var s in stashes) Stashes.Add(s);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task CommitAsync()
    {
        if (ChangedFiles.Count == 0)
        {
            StatusMessage = "Nothing to commit.";
            return;
        }
        if (string.IsNullOrWhiteSpace(CommitMessage))
        {
            StatusMessage = "Enter a commit message first.";
            return;
        }

        var result = await _git.CommitAllAsync(Path, CommitMessage.Trim());
        Report(result, "Committed all changes.", "Commit failed");
        if (result.Success) CommitMessage = string.Empty;

        await LoadChangesAsync();
        await RefreshStatusAsync();
        await LoadCommitsAsync();
    }

    [RelayCommand]
    public async Task DiscardFileAsync(FileChangeInfo? file)
    {
        file ??= SelectedChangedFile;
        if (file == null) return;

        var confirm = MessageBox.Show(
            $"Discard changes to '{file.Path}'?\n\nThis cannot be undone.",
            "Discard changes", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        Report(await _git.DiscardFileAsync(Path, file), $"Discarded {file.Path}.", "Discard failed");
        await LoadChangesAsync();
        await RefreshStatusAsync();
    }

    [RelayCommand]
    public async Task DiscardAllAsync()
    {
        var confirm = MessageBox.Show(
            "Discard ALL uncommitted changes in this repo, including untracked files?\n\nThis cannot be undone.",
            "Discard all changes", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        Report(await _git.DiscardAllAsync(Path), "Discarded all changes.", "Discard failed");
        await LoadChangesAsync();
        await RefreshStatusAsync();
    }

    [RelayCommand]
    public async Task StashAsync()
    {
        var result = await _git.StashAsync(Path, StashMessage);
        Report(result, "Changes stashed.", "Stash failed");
        if (result.Success) StashMessage = string.Empty;

        await LoadChangesAsync();
        await RefreshStatusAsync();
    }

    [RelayCommand]
    public async Task StashApplyAsync(StashInfo? stash)
    {
        if (stash == null) return;
        Report(await _git.StashApplyAsync(Path, stash.Reference), $"Applied {stash.Reference}.", "Stash apply failed");
        await LoadChangesAsync();
        await RefreshStatusAsync();
    }

    [RelayCommand]
    public async Task StashDropAsync(StashInfo? stash)
    {
        if (stash == null) return;

        var confirm = MessageBox.Show(
            $"Drop stash '{stash.Message}'?\n\nThis cannot be undone.",
            "Drop stash", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        Report(await _git.StashDropAsync(Path, stash.Reference), $"Dropped {stash.Reference}.", "Stash drop failed");
        await LoadChangesAsync();
    }

    [RelayCommand]
    public async Task ResolveOursAsync(FileChangeInfo? file)
    {
        file ??= SelectedChangedFile;
        if (file == null) return;
        Report(await _git.ResolveConflictAsync(Path, file.Path, ConflictResolution.Ours),
            $"Resolved {file.Path} using ours.", "Resolve failed");
        await LoadChangesAsync();
    }

    [RelayCommand]
    public async Task ResolveTheirsAsync(FileChangeInfo? file)
    {
        file ??= SelectedChangedFile;
        if (file == null) return;
        Report(await _git.ResolveConflictAsync(Path, file.Path, ConflictResolution.Theirs),
            $"Resolved {file.Path} using theirs.", "Resolve failed");
        await LoadChangesAsync();
    }

    [RelayCommand]
    public async Task MarkResolvedAsync(FileChangeInfo? file)
    {
        file ??= SelectedChangedFile;
        if (file == null) return;
        Report(await _git.MarkResolvedAsync(Path, file.Path), $"Marked {file.Path} resolved.", "Mark resolved failed");
        await LoadChangesAsync();
    }

    [RelayCommand]
    public async Task AbortMergeAsync()
    {
        var confirm = MessageBox.Show(
            "Abort the in-progress merge?\n\nAny conflict resolution done so far will be discarded.",
            "Abort merge", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        Report(await _git.AbortMergeAsync(Path), "Merge aborted.", "Abort failed");
        await LoadChangesAsync();
        await RefreshStatusAsync();
    }

    [RelayCommand]
    public async Task CommitMergeAsync()
    {
        Report(await _git.CommitMergeAsync(Path), "Merge completed.", "Merge commit failed");
        await LoadChangesAsync();
        await RefreshStatusAsync();
        await LoadCommitsAsync();
    }

    [RelayCommand]
    public void OpenFileInEditor(FileChangeInfo? file)
    {
        file ??= SelectedChangedFile;
        if (file == null) return;

        var fullPath = System.IO.Path.Combine(Path, file.Path);
        try
        {
            Process.Start(new ProcessStartInfo("code", $"\"{fullPath}\"") { UseShellExecute = true });
        }
        catch
        {
            Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
        }
    }

    [RelayCommand]
    public async Task LoadDevicesAsync()
    {
        if (Kind != ProjectKind.Flutter) return;

        IsBusy = true;
        try
        {
            var devices = await _flutter.GetDevicesAsync();
            Devices.Clear();
            foreach (var d in devices) Devices.Add(d);
            SelectedDevice ??= Devices.FirstOrDefault();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task LoadEnvironmentAsync()
    {
        IsBusy = true;
        try
        {
            List<EnvEntry> entries = Kind switch
            {
                ProjectKind.DotNet => _envParser.ParseDotNetProject(Path, Info.CsprojOverride ?? _dotNet.FindCsproj(Path)),
                ProjectKind.Flutter => _envParser.ParseFlutterProject(Path, Info.EnvFileOverride),
                _ => new List<EnvEntry>(),
            };

            EnvEntries.Clear();
            foreach (var e in entries) EnvEntries.Add(e);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public void Run() => StartRun(string.IsNullOrWhiteSpace(Info.RunCommandOverride) ? null : Info.RunCommandOverride.Trim());

    /// <summary>Runs one of the saved start commands; the box's command stays as it is.</summary>
    [RelayCommand(CanExecute = nameof(CanRunSaved))]
    private void RunSaved(string command) => StartRun(command);

    private bool CanRunSaved(string? command) => !IsRunning;

    private bool CanSaveStartCommand() => StartCommand.Trim() is { Length: > 0 } command && !SavedCommands.Contains(command);

    /// <summary>Keeps the box's command in the saved list, to run it again with one click.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveStartCommand))]
    private void SaveStartCommand()
    {
        SavedCommands.Add(StartCommand.Trim());
        SavedCommandsChanged();
    }

    [RelayCommand]
    private void RemoveSavedCommand(string command)
    {
        SavedCommands.Remove(command);
        SavedCommandsChanged();
    }

    private void SavedCommandsChanged()
    {
        Info.SavedRunCommands = SavedCommands.Count == 0 ? null : SavedCommands.ToList();
        SettingsChanged?.Invoke();
        SaveStartCommandCommand.NotifyCanExecuteChanged();
    }

    // customCommand is the box's command or a saved one; null runs the kind's own (flutter run / dotnet run).
    private void StartRun(string? customCommand)
    {
        if (IsRunning) return;

        string command;
        List<int> ports = new();
        switch (Kind)
        {
            case ProjectKind.Flutter:
                if (customCommand == null && SelectedDevice == null)
                {
                    ConsoleOutput.Add("[dashboard] Select a device before running.");
                    return;
                }
                command = customCommand ?? _flutter.BuildRunCommand(SelectedDevice!.Id);
                break;

            case ProjectKind.DotNet:
                var csproj = Info.CsprojOverride ?? _dotNet.FindCsproj(Path);
                if (csproj == null && customCommand == null)
                {
                    ConsoleOutput.Add("[dashboard] No .csproj found for this project. Type a start command above to run it another way.");
                    return;
                }
                command = customCommand ?? _dotNet.BuildRunCommand(csproj!);
                if (csproj != null) ports = _dotNet.GetLaunchPorts(csproj);
                break;

            default:
                if (customCommand == null)
                {
                    ConsoleOutput.Add("[dashboard] Type the command that starts this project in START COMMAND above, then press Run.");
                    return;
                }
                command = customCommand;
                break;
        }

        ConsoleOutput.Clear();
        _lastLineIsPartial = false;
        if (!EnsurePortsFree(ports)) return;
        ConsoleOutput.Add($"[dashboard] {command}");
        DetectedUrls.Clear();
        IsRunning = true;

        _runningProcess = _processRunner.StartStreaming(
            Path,
            command,
            onOutput: (line, partial) => Application.Current.Dispatcher.Invoke(() =>
            {
                ShowOutput(line, partial);
                if (partial) return;
                // Flutter prints VM-service and DevTools addresses, which aren't the app; leave those out.
                var url = Kind switch
                {
                    ProjectKind.DotNet => _dotNet.TryExtractListeningUrl(line),
                    ProjectKind.Flutter => null,
                    _ => TryExtractLocalUrl(line),
                };
                if (url != null && !DetectedUrls.Contains(url)) DetectedUrls.Add(url);
            }),
            onExit: code => Application.Current.Dispatcher.Invoke(() =>
            {
                ConsoleOutput.Add($"[dashboard] process exited with code {code}");
                _lastLineIsPartial = false;
                IsRunning = false;
            }));

        if (Kind == ProjectKind.Flutter && SelectedDevice != null) RunStartedOnDevice?.Invoke(SelectedDevice);
    }

    private static string? TryExtractLocalUrl(string line)
    {
        var match = LocalUrlPattern.Match(AnsiEscape.Replace(line, string.Empty));
        if (!match.Success) return null;

        // 0.0.0.0 and [::] mean "every interface": not an address a browser can open, but localhost reaches the server.
        return match.Value.TrimEnd('.', ',', ')')
            .Replace("0.0.0.0", "localhost")
            .Replace("[::]", "localhost");
    }

    // A server left over from a crashed run or started from another terminal keeps the port bound,
    // and Kestrel then fails with "address already in use" — offer to force stop it first.
    private bool EnsurePortsFree(List<int> ports)
    {
        var listeners = _ports.FindListeners(ports);
        if (listeners.Count == 0) return true;

        var details = string.Join("\n", listeners.Select(l => $"  • Port {l.Port}: {l.ProcessName} (PID {l.Pid})"));
        var confirm = MessageBox.Show(
            $"These ports are already in use:\n\n{details}\n\nForce stop these processes and run?",
            "Port in use", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            ConsoleOutput.Add($"[dashboard] Run cancelled — port {string.Join(", ", listeners.Select(l => l.Port))} in use.");
            return false;
        }

        foreach (var listener in listeners.DistinctBy(l => l.Pid))
        {
            if (!_ports.TryKillProcess(listener.Pid, out var error))
            {
                ConsoleOutput.Add($"[dashboard] Could not stop {listener.ProcessName} (PID {listener.Pid}): {error}");
                return false;
            }
            ConsoleOutput.Add($"[dashboard] Stopped {listener.ProcessName} (PID {listener.Pid}) on port {listener.Port}.");
        }
        return true;
    }

    // Picking the script fills in the command that runs it (python app.py, node server.js, ...), which can still be edited.
    [RelayCommand]
    public void ChooseStartFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Choose the file that starts {Name}",
            InitialDirectory = Path,
            Filter = Kind switch
            {
                ProjectKind.Python => "Python files (*.py;*.pyw)|*.py;*.pyw|All files (*.*)|*.*",
                ProjectKind.Node => "JavaScript and TypeScript (*.js;*.mjs;*.cjs;*.ts;*.mts;*.cts)|*.js;*.mjs;*.cjs;*.ts;*.mts;*.cts|All files (*.*)|*.*",
                _ => "Scripts and programs|*.py;*.pyw;*.js;*.mjs;*.cjs;*.ts;*.bat;*.cmd;*.ps1;*.sh;*.exe;*.rb;*.php;*.go;*.dart;*.java|All files (*.*)|*.*",
            },
        };
        if (dialog.ShowDialog() != true) return;

        Info.RunCommandOverride = StartCommandBuilder.Build(Path, dialog.FileName);
        OnPropertyChanged(nameof(StartCommand));
        SaveStartCommandCommand.NotifyCanExecuteChanged();
        SettingsChanged?.Invoke();

        // A project the scan couldn't classify is told apart by the file it starts from.
        if (Kind == ProjectKind.Unknown)
        {
            var kind = StartCommandBuilder.KindFor(dialog.FileName);
            if (kind != ProjectKind.Unknown) Kind = kind;
        }
    }

    [RelayCommand]
    public void Stop()
    {
        _runningProcess?.Stop();
        IsRunning = false;
    }

    /// <summary>Sends the input box's line to the running app, as if typed in its terminal. An empty line is just Enter.</summary>
    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void SendInput()
    {
        if (SendToApp(ConsoleInput)) ConsoleInput = string.Empty;
    }

    /// <summary>A one-key command for the running app, such as flutter run's r (hot reload) and R (hot restart).</summary>
    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void SendKey(string key) => SendToApp(key);

    private bool SendToApp(string text)
    {
        if (_runningProcess == null) return false;
        if (!_runningProcess.SendInput(text))
        {
            ConsoleOutput.Add("[dashboard] The app isn't reading input any more.");
            _lastLineIsPartial = false;
            return false;
        }

        // Shown as a terminal echoes it: after the prompt it answers, else on its own line.
        if (_lastLineIsPartial) ConsoleOutput[ConsoleOutput.Count - 1] += text;
        else ConsoleOutput.Add(text);
        _lastLineIsPartial = false;
        return true;
    }

    // A partial line stays the console's last line, and the next output replaces it with more of the same line.
    private void ShowOutput(string line, bool partial)
    {
        if (_lastLineIsPartial) ConsoleOutput[ConsoleOutput.Count - 1] = line;
        else ConsoleOutput.Add(line);
        _lastLineIsPartial = partial;
    }

    /// <summary>The repo's page on GitHub, from its GitHub remote (origin first); null when it isn't on GitHub.</summary>
    public string? GitHubUrl => GitHubRemote.Find(Path) is { } repo
        ? $"https://github.com/{Uri.EscapeDataString(repo.Owner)}/{Uri.EscapeDataString(repo.Name)}"
        : null;

    public string GitHubToolTip => GitHubUrl ?? "This repo has no GitHub remote, so it isn't on GitHub";

    [RelayCommand(CanExecute = nameof(IsOnGitHub))]
    private void OpenInGitHub() => OpenUrl(GitHubUrl);

    private bool IsOnGitHub() => GitHubUrl != null;

    [RelayCommand]
    public void OpenInExplorer()
    {
        if (Directory.Exists(Path))
            Process.Start("explorer.exe", Path);
    }

    [RelayCommand]
    public Task OpenInVisualStudioAsync() => OpenInIdeAsync(Ide.VisualStudio, "Visual Studio", VisualStudioTarget);

    // The project root, not android/: with the Flutter plugin Android Studio then opens the whole app.
    [RelayCommand]
    public Task OpenInAndroidStudioAsync() => OpenInIdeAsync(Ide.AndroidStudio, "Android Studio", () => Path);

    [RelayCommand]
    public Task OpenInVsCodeAsync() => OpenInIdeAsync(Ide.VisualStudioCode, "VS Code", () => Path);

    // A solution (or project) loads into Visual Studio ready to build and debug; a bare folder only opens in folder view.
    private string VisualStudioTarget() =>
        _dotNet.FindSolution(Path)
        ?? (Kind == ProjectKind.DotNet ? Info.CsprojOverride ?? _dotNet.FindCsproj(Path) : null)
        ?? Path;

    private async Task OpenInIdeAsync(Ide ide, string ideName, Func<string> chooseTarget)
    {
        try
        {
            // Finding the IDE runs vswhere and searching for the solution walks the repo, so keep both off the UI thread.
            var (executable, target) = await Task.Run(() => (_ides.FindExecutable(ide), chooseTarget()));
            if (executable == null)
            {
                StatusMessage = $"{ideName} is not installed.";
                return;
            }

            _ides.Open(executable, target);
            StatusMessage = $"Opened {System.IO.Path.GetFileName(target)} in {ideName}.";
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Could not open {ideName}: {ex.Message}";
        }
    }

    [RelayCommand]
    public void OpenInApp(OpenWithApp? app)
    {
        if (app == null) return;
        try
        {
            var arguments = app.Arguments.Replace(OpenWithApp.PathPlaceholder, Path, StringComparison.OrdinalIgnoreCase);
            _ides.OpenWith(app.Program, arguments, Path);
            StatusMessage = $"Opened {Name} in {app.Name}.";
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusMessage = $"Could not open {app.Name}: {ex.Message}";
        }
    }

    /// <summary>The New branch dialog's view model, starting from this project's current branch.</summary>
    public NewBranchViewModel NewBranch() => new(Path, Branch, IsDirty, _git);

    /// <summary>After the New branch dialog made one: says so and shows where things stand now.</summary>
    public async Task BranchCreatedAsync(string message)
    {
        await RefreshStatusAsync();
        StatusMessage = NeedsPublish ? $"{message} Press Publish branch to put it on {PublishRemote}." : message;
        await LoadChangesAsync();
        await LoadCommitsAsync();
    }

    /// <summary>The Create pull request dialog's view model, for this project's current branch.</summary>
    public CreatePullRequestViewModel NewPullRequest((string Owner, string Name) repo, IGitHubService gitHub) =>
        new(Path, Branch, repo, _git, gitHub);

    [RelayCommand]
    public void OpenUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
