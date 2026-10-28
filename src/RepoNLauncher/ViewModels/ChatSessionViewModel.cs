using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoNLauncher.Models;
using RepoNLauncher.Services;

namespace RepoNLauncher.ViewModels;

/// <summary>
/// One chat with Claude Code or Codex: its messages, the composer, and the model, effort, permission and folder it
/// runs with. The agent and folder are fixed once the chat has started; the rest can change between turns.
/// </summary>
public sealed partial class ChatSessionViewModel : ObservableObject
{
    public const string NewChatTitle = "New chat";

    private readonly AgentChatViewModel _owner;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private ChatTranscript _transcript;
    private AgentTurn? _turn;
    private DateTime _turnStarted;
    private string? _workingDetail;
    private IReadOnlyList<CodexModel> _codexModels = [];

    public ChatSessionViewModel(AgentChatViewModel owner, AgentKind agent, string workingDirectory, ChatSessionSummary? saved = null)
    {
        _owner = owner;
        _agent = agent;
        _workingDirectory = workingDirectory;
        Saved = saved;
        if (saved != null)
        {
            _sessionId = saved.Id;
            _title = saved.Title;
        }
        _transcript = new ChatTranscript(_items);
        _clock.Tick += (_, _) => UpdateWorkingText();
        Attachments.CollectionChanged += (_, _) => SendCommand.NotifyCanExecuteChanged();
        LoadOptions();
        _isAgentMissing = !owner.IsInstalled(agent);
    }

    /// <summary>The saved chat this was opened from, or the one it became after its first turn.</summary>
    public ChatSessionSummary? Saved { get; set; }

    [ObservableProperty] private ObservableCollection<ChatItemViewModel> _items = new();
    public ObservableCollection<ChatAttachment> Attachments { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AgentName), nameof(AgentLetter), nameof(AgentValue), nameof(Placeholder), nameof(InstallUrl), nameof(MissingText))]
    private AgentKind _agent;

    [ObservableProperty] private string _title = NewChatTitle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStarted))]
    private string? _sessionId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProjectName), nameof(Placeholder))]
    private string _workingDirectory;

    /// <summary>The model the agent reported using, e.g. claude-opus-5-5.</summary>
    [ObservableProperty] private string? _modelInUse;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _draft = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStarted))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(StopCommand))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool _isLoading;

    [ObservableProperty] private string _workingText = string.Empty;
    [ObservableProperty] private bool _isAgentMissing;

    [ObservableProperty] private IReadOnlyList<AgentOption> _models = [];
    [ObservableProperty] private IReadOnlyList<AgentOption> _efforts = [];
    [ObservableProperty] private IReadOnlyList<AgentOption> _modes = [];
    [ObservableProperty] private AgentOption? _model;
    [ObservableProperty] private AgentOption? _effort;
    [ObservableProperty] private AgentOption? _mode;

    /// <summary>Once a chat has a session (or is starting one), its agent and folder can't change.</summary>
    public bool IsStarted => SessionId != null || IsRunning;

    public string AgentName => Agent == AgentKind.Claude ? "Claude" : "Codex";
    public string AgentLetter => Agent == AgentKind.Claude ? "C" : "X";

    public string MissingText => Agent == AgentKind.Claude
        ? "Claude Code isn't installed on this PC. Install it, then send again."
        : "Codex isn't installed on this PC. Install the Codex app or Codex CLI, then send again.";

    /// <summary>For the agent picker, whose choices are keyed by name.</summary>
    public string AgentValue
    {
        get => Agent.ToString();
        set
        {
            if (Enum.TryParse<AgentKind>(value, out var agent) && !IsStarted) Agent = agent;
        }
    }

    public string ProjectName => Path.GetFileName(WorkingDirectory.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : WorkingDirectory;

    public string Placeholder => $"Ask {AgentName} to do something in {ProjectName}.  @ mentions a file, Enter sends, Shift+Enter adds a line.";

    public string InstallUrl => Agent == AgentKind.Claude ? AgentCli.ClaudeInstallUrl : AgentCli.CodexInstallUrl;

    partial void OnAgentChanged(AgentKind value)
    {
        LoadOptions();
        IsAgentMissing = !_owner.IsInstalled(value);
    }

    // Each Codex model has its own effort levels.
    partial void OnModelChanged(AgentOption? value)
    {
        if (Agent != AgentKind.Codex) return;
        var efforts = _codexModels.FirstOrDefault(m => m.Option.Value == value?.Value)?.Efforts ?? _codexModels.FirstOrDefault()?.Efforts ?? [];
        var current = Effort?.Value;
        Efforts = efforts;
        Effort = efforts.FirstOrDefault(e => e.Value == current) ?? efforts.FirstOrDefault();
    }

    private void LoadOptions()
    {
        var last = _owner.LastChoices(Agent);
        if (Agent == AgentKind.Claude)
        {
            Models = AgentOptions.ClaudeModels;
            Efforts = AgentOptions.ClaudeEfforts;
            Modes = AgentOptions.ClaudeModes;
        }
        else
        {
            _codexModels = AgentOptions.CodexModels();
            Models = _codexModels.Select(m => m.Option).ToList();
            Efforts = _codexModels[0].Efforts;
            Modes = AgentOptions.CodexModes;
        }
        Model = Models.FirstOrDefault(m => m.Value == last?.Model) ?? Models[0];
        Effort = Efforts.FirstOrDefault(e => e.Value == last?.Effort) ?? Efforts[0];
        var defaultMode = Agent == AgentKind.Claude ? AgentOptions.ClaudeAskMode : AgentOptions.CodexDefaultMode;
        Mode = Modes.FirstOrDefault(m => m.Value == (last?.Mode ?? defaultMode)) ?? Modes[0];
    }

    /// <summary>Reads a saved chat's messages in the background.</summary>
    public async Task LoadAsync()
    {
        if (Saved is not { } saved) return;
        IsLoading = true;
        try
        {
            var items = await Task.Run(() =>
            {
                var list = new List<ChatItemViewModel>();
                var transcript = new ChatTranscript(list);
                foreach (var e in ChatHistoryService.Load(saved)) transcript.Apply(e);
                transcript.EndTurn();
                return list;
            });
            // Anything sent while it loaded stays at the end.
            foreach (var item in Items) items.Add(item);
            Items = new ObservableCollection<ChatItemViewModel>(items);
            _transcript = new ChatTranscript(Items);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void Send()
    {
        var prompt = Draft.Trim();
        var program = _owner.ProgramFor(Agent);
        if (program == null)
        {
            IsAgentMissing = true;
            Items.Add(new NoticeChatItem($"{(Agent == AgentKind.Claude ? "Claude Code" : "Codex")} isn't installed on this PC.", isError: true));
            return;
        }
        if (!Directory.Exists(WorkingDirectory))
        {
            Items.Add(new NoticeChatItem($"The folder {WorkingDirectory} doesn't exist anymore.", isError: true));
            return;
        }

        var attachments = Attachments.ToList();
        var request = new ChatTurnRequest(prompt, attachments, WorkingDirectory, SessionId,
            Model?.Value ?? string.Empty, Effort?.Value ?? string.Empty, Mode?.Value ?? Modes[0].Value);

        Items.Add(new UserChatItem(prompt,
            attachments.Where(a => a.IsImage).Select(a => new ChatImage(null, a.Path)).ToList(),
            attachments.Where(a => !a.IsImage).Select(a => a.Name).ToList()));
        if (Title == NewChatTitle) Title = CodexItems.Title(prompt.Length > 0 ? prompt : attachments[0].Name);
        Draft = string.Empty;
        Attachments.Clear();
        _owner.RememberChoices(this);

        IsRunning = true;
        _turnStarted = DateTime.Now;
        _workingDetail = null;
        UpdateWorkingText();
        _clock.Start();

        void OnEvent(AgentEvent e) => _dispatcher.InvokeAsync(() => Apply(e));
        _turn = Agent == AgentKind.Claude ? new ClaudeChatTurn(program, request, OnEvent) : new CodexChatTurn(program, request, OnEvent);
    }

    private bool CanSend() => !IsRunning && !IsLoading && (Draft.Trim().Length > 0 || Attachments.Count > 0);

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop() => _turn?.Stop();

    [RelayCommand]
    private void RemoveAttachment(ChatAttachment attachment) => Attachments.Remove(attachment);

    public void AddAttachments(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(File.Exists))
            if (!Attachments.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)))
                Attachments.Add(new ChatAttachment(path));
    }

    /// <summary>Plan approval and "allow all edits" switch the mode for the rest of the chat; the picker follows.</summary>
    public void SetMode(string mode)
    {
        if (Modes.FirstOrDefault(m => m.Value == mode) is { } option) Mode = option;
    }

    private void Apply(AgentEvent e)
    {
        switch (e)
        {
            case SessionStartedEvent started:
                SessionId = started.SessionId;
                if (started.Model != null) ModelInUse = started.Model;
                break;
            case StatusEvent status:
                _workingDetail = status.Text;
                UpdateWorkingText();
                break;
            case PermissionRequestEvent request:
                AddPrompt(request);
                break;
            case TurnFinishedEvent finished:
                FinishTurn(finished);
                break;
            default:
                _transcript.Apply(e);
                break;
        }
    }

    private void AddPrompt(PermissionRequestEvent request)
    {
        var turn = _turn;
        var tool = _transcript.Tool(request.ToolUseId);
        if (tool != null) tool.Status = ToolStatus.Waiting;

        void Answer(JsonObject response)
        {
            if (tool is { Status: ToolStatus.Waiting }) tool.Status = ToolStatus.Running;
            turn?.Answer(request.RequestId, response);
        }

        ChatItemViewModel item = request.ToolName switch
        {
            "AskUserQuestion" => new QuestionChatItem(request.Input, Answer),
            "ExitPlanMode" => new PlanChatItem(request.Input, Answer, SetMode),
            _ => new PermissionChatItem(ClaudeTools.Describe(request.ToolName, JsonInput.ToElement(request.Input), WorkingDirectory),
                request.Input, request.Suggestions, Answer, SetMode),
        };
        Items.Add(item);
    }

    private void FinishTurn(TurnFinishedEvent finished)
    {
        _clock.Stop();
        _turn = null;
        IsRunning = false;
        WorkingText = string.Empty;
        _transcript.EndTurn();
        foreach (var prompt in Items.OfType<PromptChatItem>()) prompt.Expire();

        if (finished.IsError) Items.Add(new NoticeChatItem(finished.Message ?? "Something went wrong.", isError: true));
        else Items.Add(new NoticeChatItem(finished.Message ?? $"Worked for {Duration(finished.Duration ?? DateTime.Now - _turnStarted)}"));
        _owner.OnTurnFinished(this);
    }

    private void UpdateWorkingText()
    {
        if (!IsRunning) return;
        var text = $"{AgentName} is working · {Duration(DateTime.Now - _turnStarted)}";
        WorkingText = _workingDetail is { Length: > 0 } detail ? $"{text} · {detail}" : text;
    }

    private static string Duration(TimeSpan time) => time.TotalSeconds switch
    {
        < 1 => "under a second",
        < 60 => $"{(int)time.TotalSeconds}s",
        < 3600 => $"{(int)time.TotalMinutes}m {time.Seconds}s",
        _ => $"{(int)time.TotalHours}h {time.Minutes}m",
    };
}

/// <summary>
/// Turns events into chat items: streamed text grows one reply, tool results land on their tool call. Works on the
/// chat's live list, or on a plain list when a saved chat is read in the background.
/// </summary>
public sealed class ChatTranscript(IList<ChatItemViewModel> items)
{
    private readonly Dictionary<string, ToolChatItem> _tools = new();
    private AssistantChatItem? _streaming;

    public ToolChatItem? Tool(string id) => _tools.GetValueOrDefault(id);

    public void Apply(AgentEvent e)
    {
        switch (e)
        {
            case UserMessageEvent user:
                EndStreaming();
                items.Add(new UserChatItem(user.Text, user.Images, []));
                break;
            case TextDeltaEvent delta:
                if (_streaming == null)
                {
                    _streaming = new AssistantChatItem(string.Empty) { IsStreaming = true };
                    items.Add(_streaming);
                }
                _streaming.Text += delta.Text;
                break;
            case TextEvent text when _streaming != null:
                _streaming.Text = text.Text;
                EndStreaming();
                break;
            case TextEvent text:
                items.Add(new AssistantChatItem(text.Text));
                break;
            case ThinkingEvent thinking:
                EndStreaming();
                items.Add(new ThinkingChatItem(thinking.Text));
                break;
            case ToolStartedEvent started when _tools.TryGetValue(started.Id, out var existing):
                existing.Tool = started.Tool;
                break;
            case ToolStartedEvent started:
                EndStreaming();
                var tool = new ToolChatItem(started.Tool);
                _tools[started.Id] = tool;
                items.Add(tool);
                break;
            case ToolFinishedEvent finished:
                _tools.GetValueOrDefault(finished.Id)?.Finish(finished.Output, finished.IsError);
                break;
            case NoticeEvent notice:
                EndStreaming();
                items.Add(new NoticeChatItem(notice.Text, notice.IsError));
                break;
        }
    }

    /// <summary>A turn that ended (or was stopped) leaves nothing marked as still running.</summary>
    public void EndTurn()
    {
        EndStreaming();
        foreach (var tool in _tools.Values.Where(t => t.Status is ToolStatus.Running or ToolStatus.Waiting)) tool.Status = ToolStatus.Done;
    }

    private void EndStreaming()
    {
        if (_streaming == null) return;
        _streaming.IsStreaming = false;
        _streaming = null;
    }
}
