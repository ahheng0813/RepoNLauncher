using System.Collections.ObjectModel;
using System.Windows.Forms;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoNLauncher.Models;
using RepoNLauncher.Services;

namespace RepoNLauncher.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IDashboardConfigService _configService;
    private readonly IProjectScanner _scanner;
    private readonly IGitService _git;
    private readonly IFlutterService _flutter;
    private readonly IDotNetService _dotNet;
    private readonly IEnvironmentParser _envParser;
    private readonly IProcessRunner _processRunner;
    private readonly IPortService _ports;
    private readonly IIdeService _ides;

    private DashboardConfig _config;

    public ObservableCollection<ProjectViewModel> Projects { get; } = new();

    /// <summary>What the project list shows: <see cref="Projects"/> arranged into folder sections by <see cref="ProjectTreeBuilder"/>.</summary>
    public ObservableCollection<object> ProjectTree { get; } = new();

    /// <summary>User-added programs for the OPEN IN row, shared by every project.</summary>
    public ObservableCollection<OpenWithApp> OpenWithApps { get; } = new();

    public PhoneScreenViewModel PhoneScreen { get; }

    public GitHubAccountViewModel GitHub { get; }

    public PcReposViewModel PcRepos { get; }

    public AiUsageViewModel AiUsage { get; }

    public AgentChatViewModel AgentChat { get; }

    [ObservableProperty] private ProjectViewModel? _selectedProject;
    [ObservableProperty] private bool _isFetchingAll;
    [ObservableProperty] private string _globalStatus = string.Empty;

    partial void OnSelectedProjectChanged(ProjectViewModel? oldValue, ProjectViewModel? newValue)
    {
        if (oldValue != null) oldValue.IsSelected = false;
        if (newValue == null) return;

        newValue.IsSelected = true;
        _ = newValue.RefreshStatusAsync();
        _ = newValue.LoadCommitsAsync();
        _ = newValue.LoadChangesAsync();
        _ = newValue.LoadEnvironmentAsync();
        if (newValue.Kind == ProjectKind.Flutter) _ = newValue.LoadDevicesAsync();
    }

    public MainViewModel(
        IDashboardConfigService configService,
        IProjectScanner scanner,
        IGitService git,
        IFlutterService flutter,
        IDotNetService dotNet,
        IEnvironmentParser envParser,
        IProcessRunner processRunner,
        IPortService ports,
        IIdeService ides,
        IScrcpyService scrcpy,
        IGitHubService gitHub,
        AiUsageViewModel aiUsage)
    {
        _configService = configService;
        _scanner = scanner;
        _git = git;
        _flutter = flutter;
        _dotNet = dotNet;
        _envParser = envParser;
        _processRunner = processRunner;
        _ports = ports;
        _ides = ides;
        PhoneScreen = new PhoneScreenViewModel(scrcpy);
        GitHub = new GitHubAccountViewModel(gitHub);
        PcRepos = new PcReposViewModel(GitHub, IsOnDashboard, AddProjectsFromPc);
        AiUsage = aiUsage;
        AgentChat = new AgentChatViewModel(() => Projects.Select(p => new ProjectOption(p.Name, p.Path)), () => SelectedProject?.Path);

        _config = _configService.Load();
        LoadProjectsFromConfig();
        foreach (var app in _config.OpenWithApps) OpenWithApps.Add(app);

        _ = gitHub.VerifySavedSignInAsync();
    }

    public void SaveOpenWithApps(IReadOnlyList<OpenWithApp> apps)
    {
        _config.OpenWithApps = apps.ToList();
        OpenWithApps.Clear();
        foreach (var app in apps) OpenWithApps.Add(app);
        PersistConfig();
    }

    private void LoadProjectsFromConfig()
    {
        RedetectUnknownKinds();

        Projects.Clear();
        foreach (var info in _config.Projects.OrderBy(p => p.Name))
        {
            Projects.Add(CreateProjectViewModel(info));
        }

        RebuildProjectTree();
    }

    // Called after any change to the project or scan-folder lists. Project view models are reused, so a project's
    // state (selection, running app, status) carries over; sections keep their collapsed state via the config.
    private void RebuildProjectTree()
    {
        var collapsed = new HashSet<string>(_config.CollapsedGroups, StringComparer.OrdinalIgnoreCase);
        ProjectTree.Clear();
        foreach (var item in ProjectTreeBuilder.Build(Projects, _config.ScanFolders, path => !collapsed.Contains(path), OnGroupExpandedChanged))
            ProjectTree.Add(item);
    }

    private void OnGroupExpandedChanged(ProjectGroupViewModel group)
    {
        _config.CollapsedGroups.RemoveAll(p => string.Equals(p, group.Path, StringComparison.OrdinalIgnoreCase));
        if (!group.IsExpanded) _config.CollapsedGroups.Add(group.Path);
        PersistConfig();
    }

    // Projects saved before a DetectKind improvement (e.g. recognizing .slnx solutions) are stuck
    // with a stale ProjectKind.Unknown forever unless re-checked, since Kind is only set once on add.
    private void RedetectUnknownKinds()
    {
        var changed = false;
        foreach (var info in _config.Projects.Where(p => p.Kind == ProjectKind.Unknown))
        {
            var kind = _scanner.DetectKind(info.Path);
            if (kind != ProjectKind.Unknown)
            {
                info.Kind = kind;
                changed = true;
            }
        }

        if (changed) PersistConfig();
    }

    private ProjectViewModel CreateProjectViewModel(ProjectInfo info)
    {
        var project = new ProjectViewModel(info, _git, _flutter, _dotNet, _envParser, _processRunner, _ports, _ides);
        project.RunStartedOnDevice += device => _ = PhoneScreen.ShowRunDeviceAsync(device);
        project.SettingsChanged += PersistConfig; // the project edits its ProjectInfo, which is part of _config
        project.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProjectViewModel.IsChecked)) RefreshSectionCheckboxes(ProjectTree);
        };
        return project;
    }

    // A section's checkbox reflects its repos' ticks, so any tick change can flip the sections above it.
    private static void RefreshSectionCheckboxes(IEnumerable<object> items)
    {
        foreach (var group in items.OfType<ProjectGroupViewModel>())
        {
            group.RefreshIsChecked();
            RefreshSectionCheckboxes(group.Children);
        }
    }

    private void PersistConfig() => _configService.Save(_config);

    [RelayCommand]
    public async Task FetchAllAsync()
    {
        if (IsFetchingAll) return;

        IsFetchingAll = true;
        GlobalStatus = "Fetching all repositories...";
        try
        {
            var tasks = Projects
                .Where(p => _git.IsGitRepo(p.Path))
                .Select(p => p.FetchLatestAndRefreshCommitsAsync());

            await Task.WhenAll(tasks);
            GlobalStatus = $"Done. {Projects.Count} project(s) checked.";
        }
        finally
        {
            IsFetchingAll = false;
        }
    }

    [RelayCommand]
    public async Task FetchSelectedAsync()
    {
        if (IsFetchingAll) return;

        var selected = Projects.Where(p => p.IsChecked && _git.IsGitRepo(p.Path)).ToList();
        if (selected.Count == 0)
        {
            GlobalStatus = "No projects checked.";
            return;
        }

        IsFetchingAll = true;
        GlobalStatus = $"Fetching {selected.Count} selected repo(s)...";
        try
        {
            await Task.WhenAll(selected.Select(p => p.FetchLatestAndRefreshCommitsAsync()));
            GlobalStatus = $"Done. {selected.Count} selected repo(s) checked.";
        }
        finally
        {
            IsFetchingAll = false;
        }
    }

    [RelayCommand]
    public void AddProjectFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Select a project folder" };
        if (dialog.ShowDialog() != DialogResult.OK) return;

        if (AddProjectIfNew(dialog.SelectedPath, isAutoDiscovered: false))
            RebuildProjectTree();
    }

    [RelayCommand]
    public void ScanFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Select a root folder to scan for git repos" };
        if (dialog.ShowDialog() != DialogResult.OK) return;

        if (!_config.ScanFolders.Contains(dialog.SelectedPath, StringComparer.OrdinalIgnoreCase))
            _config.ScanFolders.Add(dialog.SelectedPath);

        var discovered = _scanner.Scan(dialog.SelectedPath);
        var addedCount = 0;
        foreach (var info in discovered)
        {
            if (AddProjectIfNew(info.Path, isAutoDiscovered: true)) addedCount++;
        }

        // Saved even when nothing new was added: a new scan folder alone can regroup projects already in the list.
        PersistConfig();
        RebuildProjectTree();
        GlobalStatus = $"Scan found {discovered.Count} repo(s), added {addedCount} new project(s).";
    }

    private int AddProjectsFromPc(IReadOnlyList<string> paths)
    {
        var added = paths.Count(path => AddProjectIfNew(path, isAutoDiscovered: false));
        if (added > 0) RebuildProjectTree();
        GlobalStatus = $"Added {added} repo(s) from Repos on this PC.";
        return added;
    }

    private bool IsOnDashboard(string path) =>
        _config.Projects.Any(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));

    private bool AddProjectIfNew(string path, bool isAutoDiscovered)
    {
        if (IsOnDashboard(path))
            return false;

        var info = new ProjectInfo
        {
            Path = path,
            Name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/')),
            Kind = _scanner.DetectKind(path),
            IsAutoDiscovered = isAutoDiscovered,
        };

        _config.Projects.Add(info);
        PersistConfig();

        var vm = CreateProjectViewModel(info);
        Projects.Add(vm);
        _ = vm.RefreshStatusAsync();
        return true;
    }

    [RelayCommand]
    public void RemoveProject(ProjectViewModel? project)
    {
        project ??= SelectedProject;
        if (project == null) return;

        project.Stop();
        _config.Projects.RemoveAll(p => string.Equals(p.Path, project.Path, StringComparison.OrdinalIgnoreCase));
        PersistConfig();
        Projects.Remove(project);
        RebuildProjectTree();

        if (SelectedProject == project) SelectedProject = Projects.FirstOrDefault();
    }

    [RelayCommand]
    public async Task RescanAllFoldersAsync()
    {
        var addedCount = 0;
        foreach (var folder in _config.ScanFolders.ToList())
        {
            var discovered = _scanner.Scan(folder);
            foreach (var info in discovered)
            {
                if (AddProjectIfNew(info.Path, isAutoDiscovered: true)) addedCount++;
            }
        }

        RebuildProjectTree();
        GlobalStatus = $"Rescan added {addedCount} new project(s).";
        await Task.CompletedTask;
    }
}
