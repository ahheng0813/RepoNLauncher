using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoNLauncher.Models;
using RepoNLauncher.Services;

namespace RepoNLauncher.ViewModels;

public sealed record ProjectOption(string Name, string Path)
{
    public override string ToString() => Name;
}

/// <summary>
/// The AI chat window: chats saved on this PC by Claude Code and Codex, and the chats open in it. A chat keeps running
/// when the window closes; reopening shows it where it got to.
/// </summary>
public sealed partial class AgentChatViewModel : ObservableObject
{
    private const int MaxOpenChats = 12;

    private readonly ChatHistoryService _history = new();
    private readonly Func<IEnumerable<ProjectOption>> _dashboardProjects;
    private readonly Func<string?> _selectedProjectPath;
    private readonly List<ChatSessionViewModel> _open = new();
    private readonly Dictionary<AgentKind, (string Model, string Effort, string Mode)> _lastChoices = new();
    private AgentKind? _lastAgent;
    private bool _refreshing;
    private bool _syncingSelection;
    private DateTime _lastRefresh;

    public AgentChatViewModel(Func<IEnumerable<ProjectOption>> dashboardProjects, Func<string?> selectedProjectPath)
    {
        _dashboardProjects = dashboardProjects;
        _selectedProjectPath = selectedProjectPath;
        SessionsView = CollectionViewSource.GetDefaultView(Sessions);
        SessionsView.Filter = o => o is ChatListItemViewModel item && Matches(item);
        _claudePath = AgentCli.FindClaude();
        _codexPath = AgentCli.FindCodex();
    }

    public ObservableCollection<ChatListItemViewModel> Sessions { get; } = new();
    public ICollectionView SessionsView { get; }
    public ObservableCollection<ProjectOption> Projects { get; } = new();
    public ProjectFileIndex Files { get; } = new();

    [ObservableProperty] private ChatSessionViewModel? _current;
    [ObservableProperty] private ChatListItemViewModel? _selectedSession;
    [ObservableProperty] private string _search = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAll), nameof(ShowClaude), nameof(ShowCodex))]
    private string _agentFilter = "All";

    // The sidebar's All / Claude / Codex switch.
    public bool ShowAll { get => AgentFilter == "All"; set { if (value) AgentFilter = "All"; } }
    public bool ShowClaude { get => AgentFilter == nameof(AgentKind.Claude); set { if (value) AgentFilter = nameof(AgentKind.Claude); } }
    public bool ShowCodex { get => AgentFilter == nameof(AgentKind.Codex); set { if (value) AgentFilter = nameof(AgentKind.Codex); } }
    [ObservableProperty] private bool _isLoadingHistory;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Agents))]
    private string? _claudePath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Agents))]
    private string? _codexPath;

    public IReadOnlyList<AgentOption> Agents =>
    [
        new(nameof(AgentKind.Claude), "Claude Code", ClaudePath == null ? "Not installed" : null),
        new(nameof(AgentKind.Codex), "Codex", CodexPath == null ? "Not installed" : CodexPath.Contains("WindowsApps") ? "From the Codex app" : null),
    ];

    public bool HasRunningChats => _open.Any(c => c.IsRunning);

    partial void OnSearchChanged(string value) => SessionsView.Refresh();
    partial void OnAgentFilterChanged(string value) => SessionsView.Refresh();

    partial void OnSelectedSessionChanged(ChatListItemViewModel? value)
    {
        if (value != null && !_syncingSelection) Open(value);
    }

    public bool IsInstalled(AgentKind agent) => (agent == AgentKind.Claude ? ClaudePath : CodexPath) != null;

    /// <summary>Looked up again for each turn, so installing a tool while the window is open just works.</summary>
    public string? ProgramFor(AgentKind agent)
    {
        if (agent == AgentKind.Claude) return ClaudePath = AgentCli.FindClaude();
        return CodexPath = AgentCli.FindCodex();
    }

    public (string Model, string Effort, string Mode)? LastChoices(AgentKind agent) =>
        _lastChoices.TryGetValue(agent, out var choices) ? choices : null;

    public void RememberChoices(ChatSessionViewModel chat)
    {
        _lastAgent = chat.Agent;
        _lastChoices[chat.Agent] = (chat.Model?.Value ?? string.Empty, chat.Effort?.Value ?? string.Empty, chat.Mode?.Value ?? string.Empty);
        if (!_open.Contains(chat)) _open.Add(chat);
    }

    /// <summary>Called when the window shows or comes back to the front.</summary>
    public void OnActivated()
    {
        RefreshProjects();
        if (Current == null) NewChat();
        if (DateTime.Now - _lastRefresh > TimeSpan.FromSeconds(5)) _ = RefreshHistoryAsync();
    }

    [RelayCommand]
    private void NewChat()
    {
        // An empty new chat is reused rather than piling up.
        if (Current is { IsStarted: false } fresh && fresh.Items.Count == 0)
        {
            fresh.WorkingDirectory = _selectedProjectPath() ?? fresh.WorkingDirectory;
            SelectedSession = null;
            return;
        }

        var agent = _lastAgent ?? (ClaudePath != null || CodexPath == null ? AgentKind.Claude : AgentKind.Codex);
        var folder = _selectedProjectPath() ?? Projects.FirstOrDefault()?.Path ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        EnsureProject(folder);
        Current = new ChatSessionViewModel(this, agent, folder);
        SelectedSession = null;
    }

    [RelayCommand]
    private Task RefreshHistory() => RefreshHistoryAsync();

    public void OnTurnFinished(ChatSessionViewModel chat) => _ = RefreshHistoryAsync();

    public void StopAll()
    {
        foreach (var chat in _open.Where(c => c.IsRunning)) chat.StopCommand.Execute(null);
    }

    /// <summary>Adds a folder to the project picker, named after the folder unless the dashboard has a name for it.</summary>
    public void EnsureProject(string path, string? name = null)
    {
        var index = Projects.ToList().FindIndex(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && (name == null || Projects[index].Name == name)) return;
        name ??= Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } folder ? folder : path;
        if (index < 0) Projects.Add(new ProjectOption(name, path));
        else Projects[index] = new ProjectOption(name, Projects[index].Path);
    }

    private void RefreshProjects()
    {
        foreach (var project in _dashboardProjects().OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)) EnsureProject(project.Path, project.Name);
    }

    private void Open(ChatListItemViewModel item)
    {
        var summary = item.Summary;
        var chat = _open.FirstOrDefault(c => c.Agent == summary.Agent && c.SessionId == summary.Id);
        if (chat == null)
        {
            chat = new ChatSessionViewModel(this, summary.Agent, summary.WorkingDirectory, summary);
            _open.Add(chat);
            _ = chat.LoadAsync();
            ForgetIdleChats();
        }
        item.Chat = chat;
        EnsureProject(summary.WorkingDirectory);
        Current = chat;
    }

    // Saved chats can be long; keep only the most recent few in memory. A running chat always stays.
    private void ForgetIdleChats()
    {
        while (_open.Count > MaxOpenChats && _open.FirstOrDefault(c => !c.IsRunning && c != Current) is { } idle)
        {
            _open.Remove(idle);
            foreach (var item in Sessions.Where(i => i.Chat == idle)) item.Chat = null;
        }
    }

    public async Task RefreshHistoryAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        IsLoadingHistory = Sessions.Count == 0;
        try
        {
            var (sessions, claude, codex) = await Task.Run(() => (_history.List(), AgentCli.FindClaude(), AgentCli.FindCodex()));
            _lastRefresh = DateTime.Now;
            ClaudePath = claude;
            CodexPath = codex;
            foreach (var chat in _open.Append(Current).OfType<ChatSessionViewModel>()) chat.IsAgentMissing = !IsInstalled(chat.Agent);
            Merge(sessions);
        }
        finally
        {
            _refreshing = false;
            IsLoadingHistory = false;
        }
    }

    // Updates the list in place, so its scroll position and selection survive a refresh.
    private void Merge(IReadOnlyList<ChatSessionSummary> sessions)
    {
        _syncingSelection = true;
        try
        {
            for (var i = 0; i < sessions.Count; i++)
            {
                var summary = sessions[i];
                var existing = -1;
                for (var j = i; j < Sessions.Count; j++)
                {
                    if (Sessions[j].Agent == summary.Agent && Sessions[j].Id == summary.Id)
                    {
                        existing = j;
                        break;
                    }
                }

                var chat = _open.FirstOrDefault(c => c.Agent == summary.Agent && c.SessionId == summary.Id);
                if (existing < 0)
                {
                    Sessions.Insert(i, new ChatListItemViewModel(summary) { Chat = chat });
                }
                else
                {
                    if (existing != i) Sessions.Move(existing, i);
                    Sessions[i].Summary = summary;
                    Sessions[i].Chat = chat;
                }

                if (chat != null)
                {
                    chat.Saved = summary;
                    // A title Claude Code gave the chat beats the first line of the prompt.
                    if (!chat.IsRunning) chat.Title = summary.Title;
                }
            }
            while (Sessions.Count > sessions.Count) Sessions.RemoveAt(Sessions.Count - 1);

            if (Current is { SessionId: { } id } current)
                SelectedSession = Sessions.FirstOrDefault(s => s.Agent == current.Agent && s.Id == id);
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private bool Matches(ChatListItemViewModel item)
    {
        if (AgentFilter != "All" && item.Agent.ToString() != AgentFilter) return false;
        var search = Search.Trim();
        return search.Length == 0
            || item.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
            || item.ProjectName.Contains(search, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>A saved chat in the sidebar.</summary>
public sealed partial class ChatListItemViewModel(ChatSessionSummary summary) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(ProjectName), nameof(When), nameof(Details))]
    private ChatSessionSummary _summary = summary;

    /// <summary>The open chat, when there is one: the sidebar shows whether it's running.</summary>
    [ObservableProperty] private ChatSessionViewModel? _chat;

    public AgentKind Agent => Summary.Agent;
    public string Id => Summary.Id;
    public string Title => Summary.Title;
    public string ProjectName => Path.GetFileName(Summary.WorkingDirectory.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : Summary.WorkingDirectory;
    public string When => Ago(Summary.LastActive);
    public string Details => $"{ProjectName} · {When}";

    private static string Ago(DateTime time)
    {
        var age = DateTime.Now - time;
        if (age.TotalMinutes < 1) return "just now";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes}m ago";
        if (time.Date == DateTime.Today) return $"{(int)age.TotalHours}h ago";
        if (time.Date == DateTime.Today.AddDays(-1)) return "yesterday";
        return age.TotalDays < 7 ? time.ToString("ddd") : time.ToString("d MMM");
    }
}
