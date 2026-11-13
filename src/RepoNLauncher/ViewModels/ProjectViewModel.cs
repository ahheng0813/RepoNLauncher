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

/// <summary>
/// A line of a commit's diff in the Commits tab. An object rather than the string, so lines that read the same are
/// still told apart when one of them is scrolled to.
/// </summary>
public sealed partial class CommitDiffLine : ObservableObject
{
    public CommitDiffLine(string text, string search)
    {
        Text = text;
        if (search.Length > 0 && text.Contains(search, StringComparison.OrdinalIgnoreCase)) Match = search;
    }

    public string Text { get; }

    /// <summary>The searched text, marked wherever it is in the line; null when the line doesn't have it.</summary>
    public string? Match { get; }

    /// <summary>The match Next and Previous are on.</summary>
    [ObservableProperty] private bool _isCurrent;
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

    // A server listening on every interface prints 0.0.0.0, [::], * or + (Kestrel) as its host: not an address a
    // browser can open, but localhost reaches the server.
    private static readonly Regex AnyInterfaceHost = new(@"^(https?://)(?:0\.0\.0\.0|\[::\]|\*|\+)(?=[:/]|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // The device a flutter command of your own names: flutter run -d chrome, --device-id=emulator-5554.
    private static readonly Regex DeviceArgument = new(@"(?:^|\s)(?:-d|--device-id)(?:=|\s+)[""']?(?<device>[^\s""']+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<ProjectKind> AllKinds { get; } = Enum.GetValues<ProjectKind>();

    private const int CommitSearchMax = 200;

    // How many commits the Commits tab lists at first, and how many more each time it's scrolled near the end.
    private const int CommitPageSize = 50;
    private static readonly TimeSpan CommitSearchPause = TimeSpan.FromMilliseconds(450);

    private readonly IGitService _git;
    private readonly IFlutterService _flutter;
    private readonly IDotNetService _dotNet;
    private readonly IEnvironmentParser _envParser;
    private readonly IProcessRunner _processRunner;
    private readonly IPortService _ports;
    private readonly IIdeService _ides;
    private readonly IEmulatorService _emulators;

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
            // A flutter command can name its own device, which may be running already.
            OnPropertyChanged(nameof(CanRun));
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

    /// <summary>A run opens its site as soon as it prints the address, where the Run tab's Open in picker says.</summary>
    public bool OpenSiteOnRun
    {
        get => Info.OpenSiteOnRun;
        set
        {
            if (value == Info.OpenSiteOnRun) return;
            Info.OpenSiteOnRun = value;
            OnPropertyChanged();
            SettingsChanged?.Invoke();
        }
    }

    /// <summary>
    /// Raised to show the project's site: true when its button was pressed, so the browser comes to the front; false when
    /// a run just started serving it.
    /// </summary>
    public event Action<string, bool, bool?>? SiteOpenRequested;

    /// <summary>Raised to open the project's dashboard browser window without a site, such as before it has run.</summary>
    public event Action? BrowserRequested;

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
    [NotifyPropertyChangedFor(nameof(NeedsPublish), nameof(NeedsPush), nameof(NeedsRebase))]
    [NotifyCanExecuteChangedFor(nameof(PublishBranchCommand), nameof(PushCommand), nameof(RebaseCommand))]
    private bool _isPublished = true;

    /// <summary>Where Publish branch pushes to: origin, else the repo's first remote; null when it has none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsPublish), nameof(NotPublishedText))]
    [NotifyCanExecuteChangedFor(nameof(PublishBranchCommand))]
    private string? _publishRemote;
    [ObservableProperty] private bool _isDirty;

    /// <summary>Commits on the current branch that the remote branch it pulls from doesn't have yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsPush), nameof(NeedsRebase))]
    [NotifyCanExecuteChangedFor(nameof(PushCommand), nameof(RebaseCommand))]
    private int _ahead;

    /// <summary>Commits on the remote branch it pulls from that the current branch doesn't have yet, as of the last fetch.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsPush), nameof(NeedsRebase))]
    [NotifyCanExecuteChangedFor(nameof(PushCommand), nameof(RebaseCommand))]
    private int _behind;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;
    /// <summary>The device Run starts the app on. Picking one that has a run's tab shows that tab.</summary>
    [ObservableProperty] private DeviceInfo? _selectedDevice;

    /// <summary>The run whose console shows, its tab picked. Null before the first run.</summary>
    [ObservableProperty] private RunSession? _selectedRun;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelectedFileConflicted))]
    private FileChangeInfo? _selectedChangedFile;

    [ObservableProperty] private bool _hasMergeConflict;
    [ObservableProperty] private bool _isWorkingTreeClean;
    [ObservableProperty] private string _stashMessage = string.Empty;
    [ObservableProperty] private string _commitMessage = string.Empty;

    public bool IsSelectedFileConflicted => SelectedChangedFile?.Type == FileChangeType.Conflicted;

    /// <summary>The commit picked in the Commits tab; the files it changed show under the list, a file's diff at a time.</summary>
    [ObservableProperty] private CommitInfo? _selectedCommit;
    [ObservableProperty] private FileChangeInfo? _selectedCommitFile;

    /// <summary>
    /// Narrows the Commits tab to the commits with this in their message, author, hash, changed file paths or changed
    /// lines; empty lists the recent ones.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommitMatchCommand), nameof(PreviousCommitMatchCommand))]
    private string _commitSearch = string.Empty;

    /// <summary>How the search went: searching, how many commits matched, or that none did.</summary>
    [ObservableProperty] private string _commitSearchNote = string.Empty;

    /// <summary>The diff line Next and Previous are on, scrolled to.</summary>
    [ObservableProperty] private CommitDiffLine? _currentCommitMatch;

    /// <summary>Where that line is among the shown file's matches.</summary>
    [ObservableProperty] private string _commitMatchNote = string.Empty;

    /// <summary>The next page of older commits is on its way, shown at the bottom of the list.</summary>
    [ObservableProperty] private bool _isLoadingMoreCommits;

    private CancellationTokenSource? _commitSearchTyping;
    private CancellationTokenSource? _commitsLoading;
    private CancellationTokenSource? _moreCommitsLoading;

    // What the commit list was found with; the files and diffs shown are marked with the same.
    private string _commitsSearchedFor = string.Empty;

    // The commit the listed history starts from, so the pages after the first still line up when new commits are
    // made or pulled meanwhile; null while a search is listed.
    private string? _commitsFrom;

    // The last page came back full, so there may be older commits still to list.
    private bool _hasMoreCommits;

    // What's shown under the list, so Next and Previous wait while a step's commit or file is still opening.
    private CommitInfo? _commitFilesShownFor;
    private FileChangeInfo? _commitDiffShownFor;

    private readonly List<CommitDiffLine> _commitDiffMatches = new();
    private int _commitDiffMatchIndex = -1;

    // Previous stepped back into another file or commit, so it opens on its last match rather than its first.
    private bool _landOnLastMatch;

    public ObservableCollection<CommitInfo> Commits { get; } = new();
    public ObservableCollection<FileChangeInfo> CommitFiles { get; } = new();
    public ObservableCollection<CommitDiffLine> CommitDiffLines { get; } = new();
    public ObservableCollection<DeviceInfo> Devices { get; } = new();
    /// <summary>The project's runs: in a Flutter project one per device it was run on, each a tab over the console.</summary>
    public ObservableCollection<RunSession> Runs { get; } = new();

    // The console shown before anything has run, for notes such as "Select a device".
    private readonly RunSession _noRun = new(null, "Run");

    /// <summary>The console shown: the picked run's.</summary>
    public ObservableCollection<string> ConsoleOutput => (SelectedRun ?? _noRun).ConsoleOutput;

    /// <summary>The addresses the picked run's app serves on.</summary>
    public ObservableCollection<string> DetectedUrls => (SelectedRun ?? _noRun).DetectedUrls;
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

    /// <summary>The current branch is on the remote but has commits made here that the remote doesn't, so Push is offered.
    /// Not while the remote also has new ones: the push would be turned down, so Rebase is offered instead.</summary>
    public bool NeedsPush => IsPublished && Ahead > 0 && Behind == 0;

    /// <summary>
    /// The current branch and the remote one it pulls from each have commits the other doesn't, so neither a plain pull
    /// nor a push goes through. Rebase puts the commits made here on top of the remote's.
    /// </summary>
    public bool NeedsRebase => IsPublished && Ahead > 0 && Behind > 0;

    /// <summary>A branch other than the default is picked and not saved.</summary>
    public bool IsMergePickUnsaved => SelectedMergeSource != null && SelectedMergeSource.Branch != MergeSourceFor(Branch);

    /// <summary>The run shown is running: Stop, Hot reload and the input box work on it.</summary>
    public bool IsRunning => SelectedRun?.IsRunning == true;

    /// <summary>Any of the project's runs is running, on any device.</summary>
    public bool IsAnyRunning => Runs.Any(r => r.IsRunning);

    /// <summary>Run can start: the device it would run on (or, for a project not run on devices, its one run) isn't running.</summary>
    public bool CanRun => RunOnDevice(DeviceFor(BoxCommand)?.Id) is not ({ IsRunning: true } or { IsRestarting: true });

    /// <summary>A Flutter project's runs each have a tab over the console, named after the device.</summary>
    public bool ShowRunTabs => Runs.Any(r => r.Device != null);

    /// <summary>The Run tab's input box: what's typed here goes to the running app when Enter is pressed.</summary>
    [ObservableProperty] private string _consoleInput = string.Empty;

    public string ConsoleInputHint => !IsRunning
        ? "Input for the app, once it's running"
        : SelectedRun!.Device != null
            ? $"Type here and press Enter to send it to the app on {SelectedRun.Title}"
            : "Type here and press Enter to send it to the app, e.g. an answer to its prompt";

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
        IIdeService ides,
        IEmulatorService emulators)
    {
        Info = info;
        _git = git;
        _flutter = flutter;
        _dotNet = dotNet;
        _envParser = envParser;
        _processRunner = processRunner;
        _ports = ports;
        _ides = ides;
        _emulators = emulators;
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

    /// <summary>
    /// Pushes the commits made here to the remote branch this one pulls from, so GitHub and other PCs get them. Until
    /// then they're only on this PC, though the commit list shows them.
    /// </summary>
    [RelayCommand(CanExecute = nameof(NeedsPush))]
    private async Task PushAsync()
    {
        var branch = Branch;
        var count = Ahead;
        IsBusy = true;
        StatusMessage = $"Pushing {branch}...";
        try
        {
            var result = await _git.PushToUpstreamAsync(Path);
            await RefreshStatusAsync();
            await LoadCommitsAsync();
            // "[rejected]" is git turning the push down because the remote has commits this PC doesn't.
            StatusMessage = result.Success ? $"Pushed {count} commit{(count == 1 ? "" : "s")} on {branch}."
                : result.Output.Contains("[rejected]", StringComparison.Ordinal)
                    ? $"Couldn't push {branch}: the remote has commits this PC doesn't have yet. Press Fetch Latest to bring them in, then push."
                    : $"Couldn't push {branch}: {result.Output}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Puts the commits made here on top of the ones the remote branch has, so the branch can be pulled and pushed again.</summary>
    [RelayCommand(CanExecute = nameof(NeedsRebase))]
    private async Task RebaseAsync()
    {
        var branch = Branch;
        var count = Ahead;
        IsBusy = true;
        StatusMessage = $"Rebasing {branch}...";
        try
        {
            var message = await PullRebaseAsync(branch);
            await RefreshStatusAsync();
            await LoadChangesAsync();
            await LoadCommitsAsync();
            StatusMessage = message ?? $"Rebased {branch}: your {count} commit{(count == 1 ? "" : "s")} now come after the remote's. Press Push to send them.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Pulls with the commits made here put back on top, for Rebase and for Commit's pull first. Returns why it didn't
    // work out, or null when it did. A rebase that clashes is undone, so the branch is just as it was.
    private async Task<string?> PullRebaseAsync(string branch)
    {
        var result = await _git.PullRebaseAsync(Path);
        if (_git.IsRebasing(Path))
        {
            await _git.AbortRebaseAsync(Path);
            return $"Couldn't rebase {branch}: your commits change the same lines as the remote's, so nothing was changed. "
                + "Merge them instead with git pull --no-rebase, then resolve the clash in the Changes tab.";
        }
        if (!result.Success) return $"Rebase failed: {result.Output}";
        // git still exits with 0 when the uncommitted changes it set aside clash with what came in.
        if (result.Output.Contains("autostash resulted in conflicts", StringComparison.OrdinalIgnoreCase))
            return "Pulled, but your uncommitted changes clash with what came in: resolve the conflicted files in the Changes tab. "
                + "A copy of your changes is kept in the stash list.";
        return null;
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

            await RefreshStatusAsync();
            // The pull fetched first, so ahead and behind now show both sides have commits the other doesn't.
            if (!pulled && NeedsRebase)
                message = $"Couldn't pull: {Branch} has {Ahead} commit{(Ahead == 1 ? "" : "s")} made here and the remote has {Behind} new one{(Behind == 1 ? "" : "s")}. "
                    + "Press Rebase to put yours on top of the remote's.";
            StatusMessage = message;
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

        // A newer load (more typing, a refresh) takes over from one still running, and its git searches are stopped.
        // So does a page of older commits still on its way: it belongs to the list being replaced.
        _commitsLoading?.Cancel();
        _moreCommitsLoading?.Cancel();
        _moreCommitsLoading = null;
        IsLoadingMoreCommits = false;
        var loading = _commitsLoading = new CancellationTokenSource();
        var search = CommitSearch.Trim();
        CommitSearchNote = search.Length > 0 ? "Searching..." : string.Empty;
        IsBusy = true;
        try
        {
            var selected = SelectedCommit?.Hash;
            // The history starts over from its newest page, but when the picked commit is further down than that it
            // reaches past it by a page, so it stays picked even after new commits came in above it.
            var reach = _commitsSearchedFor.Length == 0 && SelectedCommit != null ? Commits.IndexOf(SelectedCommit) + 1 : 0;
            var count = reach > CommitPageSize ? reach + CommitPageSize : CommitPageSize;
            var commits = search.Length > 0
                ? await _git.SearchCommitsAsync(Path, search, CommitSearchMax, loading.Token)
                : await _git.GetCommitsAsync(Path, "HEAD", 0, count, loading.Token);
            if (loading.IsCancellationRequested) return;

            _commitsSearchedFor = search;
            // git log lists the commit it starts from first, so the later pages carry on from this HEAD.
            _commitsFrom = search.Length > 0 ? null : commits.FirstOrDefault()?.Hash;
            _hasMoreCommits = _commitsFrom != null && commits.Count == count;
            Commits.Clear();
            foreach (var c in commits) Commits.Add(c);
            // The picked commit stays picked while it's still in the list.
            SelectedCommit = Commits.FirstOrDefault(c => c.Hash == selected);
            if (search.Length > 0)
            {
                CommitSearchNote = commits.Count switch
                {
                    0 => "No commits match.",
                    1 => "1 commit matches.",
                    >= CommitSearchMax => $"The newest {CommitSearchMax} commits that match.",
                    _ => $"{commits.Count} commits match.",
                };
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_commitsLoading == loading)
            {
                _commitsLoading = null;
                IsBusy = false;
            }
            loading.Dispose();
        }
    }

    /// <summary>
    /// Adds the next page of older commits to the end of the list, as it's scrolled near there. Once the branch's first
    /// commit is listed there's nothing more to ask for; a search lists its matches all at once.
    /// </summary>
    [RelayCommand]
    private async Task LoadMoreCommitsAsync()
    {
        // Not while the list itself is still loading, nor while the page before is still on its way.
        if (!_hasMoreCommits || _commitsFrom is not { } from || _commitsLoading != null || _moreCommitsLoading != null) return;

        var loading = _moreCommitsLoading = new CancellationTokenSource();
        IsLoadingMoreCommits = true;
        try
        {
            var commits = await _git.GetCommitsAsync(Path, from, Commits.Count, CommitPageSize, loading.Token);
            if (loading.IsCancellationRequested) return;

            foreach (var c in commits) Commits.Add(c);
            _hasMoreCommits = commits.Count == CommitPageSize;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_moreCommitsLoading == loading)
            {
                _moreCommitsLoading = null;
                IsLoadingMoreCommits = false;
            }
            loading.Dispose();
        }
    }

    partial void OnCommitSearchChanged(string value)
    {
        _commitSearchTyping?.Cancel();
        var typing = _commitSearchTyping = new CancellationTokenSource();
        _ = SearchCommitsAfterTypingAsync(typing.Token);
    }

    private async Task SearchCommitsAfterTypingAsync(CancellationToken typing)
    {
        try
        {
            await Task.Delay(CommitSearchPause, typing);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        await LoadCommitsAsync();
    }

    partial void OnSelectedCommitChanged(CommitInfo? value)
    {
        _commitFilesShownFor = null;
        SelectedCommitFile = null;
        CommitFiles.Clear();
        if (value == null) return;
        _ = LoadCommitFilesAsync(value);
    }

    private async Task LoadCommitFilesAsync(CommitInfo commit)
    {
        var search = _commitsSearchedFor;
        var files = await _git.GetCommitFilesAsync(Path, commit);
        var changing = search.Length > 0 ? await _git.GetCommitFilesChangingAsync(Path, commit, search) : [];
        if (SelectedCommit != commit) return; // a newer selection's files are already loading

        CommitFiles.Clear();
        foreach (var f in files)
        {
            f.IsSearchMatch = search.Length > 0
                && (f.Path.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || f.OriginalPath?.Contains(search, StringComparison.OrdinalIgnoreCase) == true
                    || changing.Contains(f.Path));
            CommitFiles.Add(f);
        }
        _commitFilesShownFor = commit;

        // A commit the search found opens on the file it was found in; its last one when Previous stepped back into it.
        var matching = CommitFiles.Where(f => f.IsSearchMatch);
        SelectedCommitFile = (_landOnLastMatch ? matching.LastOrDefault() : matching.FirstOrDefault()) ?? CommitFiles.FirstOrDefault();
        if (SelectedCommitFile == null) _landOnLastMatch = false;
    }

    partial void OnSelectedCommitFileChanged(FileChangeInfo? value)
    {
        _commitDiffShownFor = null;
        CommitDiffLines.Clear();
        _commitDiffMatches.Clear();
        GoToCommitMatch(-1);
        if (value == null || SelectedCommit == null) return;
        _ = LoadCommitDiffAsync(SelectedCommit, value);
    }

    private async Task LoadCommitDiffAsync(CommitInfo commit, FileChangeInfo file)
    {
        var diff = await _git.GetCommitFileDiffAsync(Path, commit, file);
        if (SelectedCommitFile != file) return; // a newer selection's diff is already loading

        CommitDiffLines.Clear();
        _commitDiffMatches.Clear();
        foreach (var text in diff.Replace("\r\n", "\n").Split('\n'))
        {
            var line = new CommitDiffLine(text, _commitsSearchedFor);
            CommitDiffLines.Add(line);
            if (line.Match != null) _commitDiffMatches.Add(line);
        }
        _commitDiffShownFor = file;

        GoToCommitMatch(_landOnLastMatch ? _commitDiffMatches.Count - 1 : 0);
        _landOnLastMatch = false;
    }

    // Marks the shown diff's match at this index as the current one and scrolls to it; -1 or past the end for none.
    private void GoToCommitMatch(int index)
    {
        if (CurrentCommitMatch != null) CurrentCommitMatch.IsCurrent = false;
        _commitDiffMatchIndex = index >= 0 && index < _commitDiffMatches.Count ? index : -1;
        CurrentCommitMatch = _commitDiffMatchIndex >= 0 ? _commitDiffMatches[_commitDiffMatchIndex] : null;
        if (CurrentCommitMatch != null) CurrentCommitMatch.IsCurrent = true;
        CommitMatchNote = CurrentCommitMatch == null ? string.Empty : $"{_commitDiffMatchIndex + 1} of {_commitDiffMatches.Count} in this file";
    }

    [RelayCommand(CanExecute = nameof(CanStepCommitMatch))]
    private Task NextCommitMatchAsync() => StepCommitMatchAsync(forward: true);

    [RelayCommand(CanExecute = nameof(CanStepCommitMatch))]
    private Task PreviousCommitMatchAsync() => StepCommitMatchAsync(forward: false);

    private bool CanStepCommitMatch() => CommitSearch.Trim().Length > 0;

    // Steps to the next (or previous) line with the searched text: in the diff shown, then in the commit's next file
    // that has it, then in the next commit the search found, round to the first one after the last.
    private async Task StepCommitMatchAsync(bool forward)
    {
        if (CommitSearch.Trim() != _commitsSearchedFor)
        {
            // Enter pressed before the search caught up with the typing.
            _commitSearchTyping?.Cancel();
            await LoadCommitsAsync();
        }
        if (_commitsSearchedFor.Length == 0 || Commits.Count == 0) return;
        // The commit or file the last step moved to is still opening.
        if (SelectedCommit != null && (_commitFilesShownFor != SelectedCommit || _commitDiffShownFor != SelectedCommitFile)) return;

        var match = _commitDiffMatchIndex + (forward ? 1 : -1);
        if (_commitDiffMatchIndex >= 0 && match >= 0 && match < _commitDiffMatches.Count)
        {
            GoToCommitMatch(match);
            return;
        }

        if (SelectedCommit != null)
        {
            var at = SelectedCommitFile == null ? -1 : CommitFiles.IndexOf(SelectedCommitFile);
            var file = forward
                ? CommitFiles.Skip(at + 1).FirstOrDefault(f => f.IsSearchMatch)
                : CommitFiles.Take(Math.Max(at, 0)).LastOrDefault(f => f.IsSearchMatch);
            if (file != null)
            {
                _landOnLastMatch = !forward;
                SelectedCommitFile = file;
                return;
            }
        }

        var next = SelectedCommit == null ? -1 : Commits.IndexOf(SelectedCommit);
        next = forward ? next + 1 : next < 0 ? Commits.Count - 1 : next - 1;
        OpenCommitMatch(Commits[(next + Commits.Count) % Commits.Count], forward);
    }

    // Opens a commit on its first match, or its last going backwards.
    private void OpenCommitMatch(CommitInfo commit, bool forward)
    {
        _landOnLastMatch = !forward;
        if (commit != SelectedCommit)
        {
            SelectedCommit = commit;
            return;
        }

        // Came round to the commit already open: the only one the search found.
        var matching = CommitFiles.Where(f => f.IsSearchMatch);
        var file = (forward ? matching.FirstOrDefault() : matching.LastOrDefault()) ?? SelectedCommitFile;
        if (file != SelectedCommitFile)
        {
            SelectedCommitFile = file;
            return;
        }
        _landOnLastMatch = false;
        GoToCommitMatch(forward ? 0 : _commitDiffMatches.Count - 1);
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
        if (!await PullBeforeCommitAsync()) return;

        var result = await _git.CommitAllAsync(Path, CommitMessage.Trim());
        Report(result, "Committed all changes.", "Commit failed");
        if (result.Success) CommitMessage = string.Empty;

        await LoadChangesAsync();
        await RefreshStatusAsync();
        if (result.Success && NeedsPush) StatusMessage += " It's only on this PC until you press Push.";
        await LoadCommitsAsync();
    }

    // A commit made on top of an old copy of the branch splits it from the remote's, and then neither a pull nor a push
    // goes through. So when the remote has new commits, offer to set the changes aside, pull, and put them back first.
    // Returns whether to go on with the commit.
    private async Task<bool> PullBeforeCommitAsync()
    {
        if (!IsPublished) return true;

        StatusMessage = "Checking the remote for new commits...";
        // Offline, the fetch fails and the check goes on with what git already knows.
        await _git.FetchAsync(Path);
        await RefreshStatusAsync();
        if (Behind == 0)
        {
            StatusMessage = string.Empty;
            return true;
        }

        var upstream = await _git.GetUpstreamAsync(Path) ?? "The remote";
        var answer = MessageBox.Show(
            $"{upstream} has {Behind} commit{(Behind == 1 ? "" : "s")} that this PC doesn't have yet.\n\n" +
            "Stash your changes, pull the latest, then put your changes back and commit?\n\n" +
            "No commits on top of the old copy; the branch then needs a Rebase before it can be pulled or pushed.",
            "Pull the latest first?", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel)
        {
            StatusMessage = "Commit cancelled.";
            return false;
        }
        if (answer == MessageBoxResult.No) return true;

        IsBusy = true;
        StatusMessage = "Pulling the latest first...";
        try
        {
            var problem = await PullRebaseAsync(Branch);
            await RefreshStatusAsync();
            await LoadChangesAsync();
            await LoadCommitsAsync();
            if (problem != null)
            {
                StatusMessage = problem;
                return false;
            }
            StatusMessage = string.Empty;
            return true;
        }
        finally
        {
            IsBusy = false;
        }
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
            // Emulators by their own names: Flutter, like Android, calls them all sdk gphone64 x86 64.
            var names = await _emulators.GetDisplayNamesAsync(devices.Where(d => d.IsEmulator).Select(d => d.Id));
            foreach (var device in devices)
            {
                if (names.TryGetValue(device.Id, out var name)) device.Name = name;
            }
            // The device picked stays picked while it's still connected.
            var picked = SelectedDevice?.Id;
            Devices.Clear();
            foreach (var d in devices) Devices.Add(d);
            SelectedDevice = Devices.FirstOrDefault(d => d.Id == picked) ?? Devices.FirstOrDefault();
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Picking a device shows its run's tab, when it has one; Run is for a device that isn't running.
    partial void OnSelectedDeviceChanged(DeviceInfo? value)
    {
        if (value != null && RunOnDevice(value.Id) is { } run) SelectedRun = run;
        RefreshRunState();
    }

    // Picking a tab picks its device too, so Run and the device list are about the run shown.
    partial void OnSelectedRunChanged(RunSession? value)
    {
        if (value?.Device is { } device && SelectedDevice?.Id != device.Id
            && Devices.FirstOrDefault(d => d.Id == device.Id) is { } listed)
            SelectedDevice = listed;
        OnPropertyChanged(nameof(ConsoleOutput));
        OnPropertyChanged(nameof(DetectedUrls));
        RefreshRunState();
    }

    private RunSession? RunOnDevice(string? deviceId) => Runs.FirstOrDefault(r => r.Device?.Id == deviceId);

    private bool IsRunningOn(DeviceInfo? device) => RunOnDevice(device?.Id) is { IsRunning: true };

    private void RefreshRunState()
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsAnyRunning));
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(ShowRunTabs));
        OnPropertyChanged(nameof(ConsoleInputHint));
        OnPropertyChanged(nameof(CanRestart));
        RestartCommand.NotifyCanExecuteChanged();
        SendInputCommand.NotifyCanExecuteChanged();
        SendKeyCommand.NotifyCanExecuteChanged();
        RunSavedCommand.NotifyCanExecuteChanged();
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
    public void Run() => StartRun(BoxCommand);

    // The START COMMAND box's command; null when it's empty, for the kind's own.
    private string? BoxCommand => string.IsNullOrWhiteSpace(Info.RunCommandOverride) ? null : Info.RunCommandOverride.Trim();

    /// <summary>Runs one of the saved start commands; the box's command stays as it is.</summary>
    [RelayCommand(CanExecute = nameof(CanRunSaved))]
    private void RunSaved(string command) => StartRun(command);

    private bool CanRunSaved(string? command) => command != null && !IsRunningOn(DeviceFor(command));

    /// <summary>
    /// The device a run goes to, for a Flutter project: the one a command of your own names with -d, when it's listed,
    /// else the one picked. Null for other projects.
    /// </summary>
    private DeviceInfo? DeviceFor(string? command)
    {
        if (Kind != ProjectKind.Flutter) return null;
        if (command != null && DeviceArgument.Match(command) is { Success: true } match)
        {
            var named = match.Groups["device"].Value;
            var device = Devices.FirstOrDefault(d => d.Id.Equals(named, StringComparison.OrdinalIgnoreCase))
                         ?? Devices.FirstOrDefault(d => d.Name.Equals(named, StringComparison.OrdinalIgnoreCase));
            if (device != null) return device;
        }
        return SelectedDevice;
    }

    // The tab a run on the device goes in: the one it had, else a new one.
    private RunSession RunTabFor(DeviceInfo? device)
    {
        if (RunOnDevice(device?.Id) is { } run) return run;

        // Two devices of one name (two emulators of one model) are told apart by their ids.
        var title = device == null ? "Run"
            : Runs.Any(r => r.Device?.Name == device.Name) ? $"{device.Name} ({device.Id})"
            : device.Name;
        run = new RunSession(device, title);
        run.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(RunSession.IsRunning) or nameof(RunSession.IsRestarting)) RefreshRunState();
        };
        Runs.Add(run);
        return run;
    }

    // A note of the dashboard's, in the console shown.
    private void ShowNote(string text) => (SelectedRun ?? _noRun).AddDashboardLine(text);

    private bool CanSaveStartCommand() => StartCommand.Trim() is { Length: > 0 } command && !SavedCommands.Contains(command);

    /// <summary>Keeps the box's command in the saved list, to run it again with one click.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveStartCommand))]
    private void SaveStartCommand()
    {
        SavedCommands.Add(StartCommand.Trim());
        SavedCommandsChanged();
    }

    /// <summary>Puts a saved command on the clipboard, to run it in a terminal or paste it somewhere.</summary>
    [RelayCommand]
    private void CopySavedCommand(string command) => ClipboardText.Set(command);

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
    // A Flutter project's run goes in its device's tab, and runs alongside those on other devices.
    private void StartRun(string? customCommand)
    {
        var device = DeviceFor(customCommand);
        if (IsRunningOn(device))
        {
            // Already running there: its tab shows instead.
            SelectedRun = RunOnDevice(device?.Id);
            return;
        }

        string command;
        List<int> ports = new();
        switch (Kind)
        {
            case ProjectKind.Flutter:
                if (customCommand == null && device == null)
                {
                    ShowNote("Select a device before running.");
                    return;
                }
                command = customCommand ?? _flutter.BuildRunCommand(device!.Id);
                break;

            case ProjectKind.DotNet:
                var csproj = Info.CsprojOverride ?? _dotNet.FindCsproj(Path);
                if (csproj == null && customCommand == null)
                {
                    ShowNote("No .csproj found for this project. Type a start command above to run it another way.");
                    return;
                }
                command = customCommand ?? _dotNet.BuildRunCommand(csproj!);
                if (csproj != null) ports = _dotNet.GetLaunchPorts(csproj);
                break;

            default:
                if (customCommand == null)
                {
                    ShowNote("Type the command that starts this project in START COMMAND above, then press Run.");
                    return;
                }
                command = customCommand;
                break;
        }

        var run = RunTabFor(device);
        SelectedRun = run;
        Launch(run, command, ports);
    }

    // Runs the command in the run's tab, from an empty console, on the run's device.
    private void Launch(RunSession run, string command, List<int> ports)
    {
        var device = run.Device;
        run.Clear();
        if (!EnsurePortsFree(run, ports)) return;
        run.Command = command;
        run.Ports = ports;
        run.AddDashboardLine(command);
        run.StartedAt = DateTime.Now;
        run.IsRunning = true;

        var generation = ++run.Generation;
        run.Process = _processRunner.StartStreaming(
            Path,
            command,
            onOutput: (line, partial) => Application.Current.Dispatcher.Invoke(() =>
            {
                if (run.Generation != generation) return;
                run.ShowOutput(line, partial);
                if (partial) return;
                // Flutter prints VM-service and DevTools addresses, which aren't the app; only its web server's
                // "lib/main.dart is being served at ..." is.
                var url = Kind switch
                {
                    ProjectKind.DotNet => _dotNet.TryExtractListeningUrl(line),
                    ProjectKind.Flutter => line.Contains("is being served at", StringComparison.OrdinalIgnoreCase)
                        ? TryExtractLocalUrl(line)
                        : null,
                    _ => TryExtractLocalUrl(line),
                };
                if (url == null) return;
                url = AnyInterfaceHost.Replace(url, "$1localhost");
                if (run.DetectedUrls.Contains(url)) return;

                run.DetectedUrls.Add(url);
                // The first address is the site; any after it (https beside http, say) stay buttons.
                if (run.DetectedUrls.Count == 1 && OpenSiteOnRun) SiteOpenRequested?.Invoke(url, false, null);
            }),
            onExit: code => Application.Current.Dispatcher.Invoke(() =>
            {
                if (run.Generation != generation) return;
                run.AddDashboardLine($"process exited with code {code}");
                run.IsRunning = false;
            }));

        if (device != null) RunStartedOnDevice?.Invoke(device);
    }

    private static string? TryExtractLocalUrl(string line)
    {
        var match = LocalUrlPattern.Match(AnsiEscape.Replace(line, string.Empty));
        return match.Success ? match.Value.TrimEnd('.', ',', ')') : null;
    }

    // A server left over from a crashed run or started from another terminal keeps the port bound,
    // and Kestrel then fails with "address already in use" — offer to force stop it first.
    private bool EnsurePortsFree(RunSession run, List<int> ports)
    {
        var listeners = _ports.FindListeners(ports);
        if (listeners.Count == 0) return true;

        var details = string.Join("\n", listeners.Select(l => $"  • Port {l.Port}: {l.ProcessName} (PID {l.Pid})"));
        var confirm = MessageBox.Show(
            $"These ports are already in use:\n\n{details}\n\nForce stop these processes and run?",
            "Port in use", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            run.AddDashboardLine($"Run cancelled — port {string.Join(", ", listeners.Select(l => l.Port))} in use.");
            return false;
        }

        foreach (var listener in listeners.DistinctBy(l => l.Pid))
        {
            if (!_ports.TryKillProcess(listener.Pid, out var error))
            {
                run.AddDashboardLine($"Could not stop {listener.ProcessName} (PID {listener.Pid}): {error}");
                return false;
            }
            run.AddDashboardLine($"Stopped {listener.ProcessName} (PID {listener.Pid}) on port {listener.Port}.");
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

    /// <summary>Stops the run shown; runs on other devices carry on.</summary>
    [RelayCommand]
    public void Stop() => StopRun(SelectedRun);

    /// <summary>
    /// Stop, then Run, for the run shown: the same command again on the same device, in the same tab, once the app has
    /// ended. A run that has already stopped just runs again.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRestart))]
    private async Task RestartAsync()
    {
        if (SelectedRun is not { Command: { } command, IsRestarting: false } run) return;
        run.IsRestarting = true;
        try
        {
            if (run.IsRunning && run.Process is { } process)
            {
                run.AddDashboardLine("Restarting…");
                StopRun(run);
                await process.WaitForExitAsync(TimeSpan.FromSeconds(10));
                // The server's ports can take a moment more to come free; the port check before the run asks if they don't.
                for (var i = 0; i < 15 && _ports.FindListeners(run.Ports).Count > 0; i++) await Task.Delay(200);
                // Closed meanwhile, or run again with Run.
                if (!Runs.Contains(run) || run.IsRunning) return;
            }
            Launch(run, command, run.Ports);
        }
        finally
        {
            run.IsRestarting = false;
        }
    }

    public bool CanRestart => SelectedRun is { Command: not null, IsRestarting: false };

    /// <summary>Stops every run, as when the project is taken off the dashboard.</summary>
    public void StopAll()
    {
        foreach (var run in Runs) StopRun(run);
    }

    private static void StopRun(RunSession? run)
    {
        if (run == null) return;
        run.Process?.Stop();
        run.IsRunning = false;
    }

    /// <summary>A run's tab's ✕: stops it if it's running, and closes the tab.</summary>
    [RelayCommand]
    private void CloseRun(RunSession? run)
    {
        var index = run == null ? -1 : Runs.IndexOf(run);
        if (run == null || index < 0) return;
        var wasShown = SelectedRun == run;
        StopRun(run);
        Runs.Remove(run);
        // The tab beside it shows next.
        if (wasShown) SelectedRun = Runs.Count == 0 ? null : Runs[Math.Min(index, Runs.Count - 1)];
        RefreshRunState();
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

    // To the app of the run shown, so Hot reload reloads the device whose tab is open.
    private bool SendToApp(string text)
    {
        if (SelectedRun is not { IsRunning: true, Process: { } process } run) return false;
        if (!process.SendInput(text))
        {
            run.AddDashboardLine("The app isn't reading input any more.");
            return false;
        }
        run.Echo(text);
        return true;
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

    /// <summary>The Create pull request dialog's view model, starting from this project's current branch.</summary>
    public CreatePullRequestViewModel NewPullRequest((string Owner, string Name) repo, IGitHubService gitHub) =>
        new(Path, Branch, repo, _git, gitHub);

    /// <summary>A URL button in the Run tab: the site, where the Open in picker says.</summary>
    [RelayCommand]
    private void OpenSite(string url) => SiteOpenRequested?.Invoke(url, true, null);

    /// <summary>A URL button's menu: the site in the dashboard's browser window, whatever Open in says.</summary>
    [RelayCommand]
    private void OpenSiteInDashboard(string url) => SiteOpenRequested?.Invoke(url, true, false);

    /// <summary>A URL button's menu: the site in the default browser, whatever Open in says.</summary>
    [RelayCommand]
    private void OpenSiteInDefaultBrowser(string url) => SiteOpenRequested?.Invoke(url, true, true);

    /// <summary>The Run tab's Open dashboard browser: the project's browser window, running or not.</summary>
    [RelayCommand]
    private void OpenBrowser() => BrowserRequested?.Invoke();

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
