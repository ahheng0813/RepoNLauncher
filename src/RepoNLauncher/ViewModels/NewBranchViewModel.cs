using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoNLauncher.Services;

namespace RepoNLauncher.ViewModels;

/// <summary>
/// The New branch dialog: a branch starting from the current one, another local branch, or a remote's copy of one,
/// switched to straight away unless that's unticked.
/// </summary>
public partial class NewBranchViewModel : ObservableObject
{
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    private readonly string _path;
    private readonly bool _hasChanges;
    private readonly IGitService _git;
    private HashSet<string> _local = [];
    private Dictionary<string, string> _onRemote = [];

    public NewBranchViewModel(string path, string currentBranch, bool hasChanges, IGitService git)
    {
        _path = path;
        _hasChanges = hasChanges;
        _git = git;
        CurrentBranch = currentBranch;
    }

    public string CurrentBranch { get; }

    /// <summary>Where the branch can start: the current branch first, then the other local ones, then the remotes'.</summary>
    public ObservableCollection<BranchOption> StartPoints { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FinalName))]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartHint))]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private BranchOption? _startPoint;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartHint))]
    private bool _switchTo = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private bool _isBusy;

    [ObservableProperty] private string? _error;

    /// <summary>The name as it will be made: spaces become dashes.</summary>
    public string FinalName => Spaces.Replace(Name.Trim(), "-");

    public string StartHint
    {
        get
        {
            if (StartPoint == null) return string.Empty;
            var from = StartPoint.Remote != null
                ? $"Starts from {StartPoint.Remote}'s {StartPoint.Name}, as of the last fetch."
                : $"Starts with what's on {StartPoint.Name} now.";
            return SwitchTo && _hasChanges ? $"{from} Your uncommitted changes come along when they don't clash." : from;
        }
    }

    /// <summary>What happened, for the project's status line, once the branch is made.</summary>
    public string? Result { get; private set; }

    public event Action? Finished;

    public async Task LoadAsync()
    {
        var branches = await _git.GetBranchesAsync(_path);
        _local = branches.Local.ToHashSet();
        _onRemote = branches.Remote.GroupBy(r => r.Name).ToDictionary(g => g.Key, g => g.First().Remote);

        StartPoints.Add(new BranchOption(CurrentBranch, null, isCurrent: true));
        foreach (var name in branches.Local.Where(n => n != CurrentBranch).Order(StringComparer.OrdinalIgnoreCase))
            StartPoints.Add(new BranchOption(name, null, isCurrent: false));
        foreach (var remote in branches.Remote.OrderBy(r => r.Remote).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            StartPoints.Add(new BranchOption(remote.Name, remote.Remote, isCurrent: false));
        StartPoint = StartPoints[0];
    }

    private bool CanCreate() => !IsBusy && FinalName.Length > 0 && StartPoint != null;

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        Error = null;
        var name = FinalName;
        var start = StartPoint!;
        if (_local.Contains(name))
        {
            Error = $"There's already a branch called {name}. Pick it under Switch to instead.";
            return;
        }
        if (_onRemote.TryGetValue(name, out var remote))
        {
            Error = $"{remote} already has a branch called {name}. Pick it under Switch to, which makes a local copy of it.";
            return;
        }

        IsBusy = true;
        try
        {
            // Commands go through cmd.exe, where " and % can't be passed safely; git doesn't want them in names anyway.
            if (name.IndexOfAny(['"', '%']) >= 0 || !await _git.IsValidBranchNameAsync(_path, name))
            {
                Error = $"\"{name}\" can't be a branch name. Use letters, numbers and - _ / . but not ~ ^ : ? * [ \\ or \"..\".";
                return;
            }

            var result = await _git.CreateBranchAsync(_path, name, start.Ref, SwitchTo);
            if (!result.Success)
            {
                Error = result.Output.Contains("would be overwritten", StringComparison.OrdinalIgnoreCase)
                    ? $"Couldn't switch to {name}: it would overwrite uncommitted changes. Commit or stash them, or start from {CurrentBranch}."
                    : $"Couldn't create {name}: {result.Output}";
                return;
            }

            Result = SwitchTo ? $"Created {name} from {start.Ref} and switched to it." : $"Created {name} from {start.Ref}.";
            Finished?.Invoke();
        }
        finally
        {
            IsBusy = false;
        }
    }
}
