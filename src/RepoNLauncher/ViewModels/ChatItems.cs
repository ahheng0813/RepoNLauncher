using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoNLauncher.Models;
using RepoNLauncher.Services;

namespace RepoNLauncher.ViewModels;

/// <summary>One entry in a chat: a message, a tool call, a question for the user, or a note.</summary>
public abstract class ChatItemViewModel : ObservableObject;

public sealed class UserChatItem(string text, IReadOnlyList<ChatImage> images, IReadOnlyList<string> files) : ChatItemViewModel
{
    private IReadOnlyList<BitmapSource>? _thumbnails;

    public string Text { get; } = text;
    public bool HasText => Text.Length > 0;
    public IReadOnlyList<string> Files { get; } = files;

    /// <summary>Decoded on first display (on the UI thread), small, and only once.</summary>
    public IReadOnlyList<BitmapSource> Thumbnails => _thumbnails ??= images.Select(Thumbnail).OfType<BitmapSource>().ToList();

    public static BitmapSource? Thumbnail(ChatImage image) => image.Data != null
        ? Thumbnail(() => new MemoryStream(image.Data))
        : image.FilePath != null ? Thumbnail(() => File.OpenRead(image.FilePath)) : null;

    public static BitmapSource? Thumbnail(Func<Stream> open, int width = 360)
    {
        try
        {
            using var stream = open();
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad; // read now, so the file isn't held open
            bitmap.DecodePixelWidth = width;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or InvalidOperationException)
        {
            return null;
        }
    }
}

public sealed partial class AssistantChatItem(string text) : ChatItemViewModel
{
    [ObservableProperty] private string _text = text;

    /// <summary>Shown as plain text while it streams in, then as formatted Markdown.</summary>
    [ObservableProperty] private bool _isStreaming;

    [RelayCommand]
    private void Copy() => ClipboardText.Set(Text);
}

public sealed partial class ThinkingChatItem(string text) : ChatItemViewModel
{
    public string Text { get; } = text;
    [ObservableProperty] private bool _isExpanded;

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}

public enum ToolStatus { Running, Waiting, Done, Failed }

public sealed partial class ToolChatItem(ToolDescription tool) : ChatItemViewModel
{
    private const int MaxOutput = 20_000;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetails))]
    private ToolDescription _tool = tool;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetails))]
    private string? _output;

    [ObservableProperty] private ToolStatus _status = ToolStatus.Running;
    [ObservableProperty] private bool _isExpanded;

    public bool HasDetails => !string.IsNullOrEmpty(Tool.Detail) || !string.IsNullOrEmpty(Output);

    public void Finish(string? output, bool isError)
    {
        Output = ClaudeTools.Clip(output?.TrimEnd(), MaxOutput);
        Status = isError ? ToolStatus.Failed : ToolStatus.Done;
    }

    [RelayCommand]
    private void Toggle()
    {
        if (HasDetails) IsExpanded = !IsExpanded;
    }
}

/// <summary>A short line between messages: how long a turn took, an error, "Stopped".</summary>
public sealed class NoticeChatItem(string text, bool isError = false) : ChatItemViewModel
{
    public string Text { get; } = text;
    public bool IsError { get; } = isError;
}

/// <summary>Something Claude needs from the user before it goes on. Answered once; the turn ending first leaves it unanswered.</summary>
public abstract partial class PromptChatItem(Action<JsonObject> answer) : ChatItemViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    private string? _outcome;

    public bool IsPending => Outcome == null;

    protected void Reply(JsonObject response, string outcome)
    {
        if (!IsPending) return;
        Outcome = outcome;
        answer(response);
    }

    public void Expire()
    {
        if (IsPending) Outcome = "Not answered";
    }
}

/// <summary>"Allow Bash?": shown when Claude wants to use a tool its permission mode doesn't already allow.</summary>
public sealed partial class PermissionChatItem : PromptChatItem
{
    private readonly JsonObject _input;
    private readonly JsonArray? _suggestions;
    private readonly Action<string>? _onModeChanged;

    public PermissionChatItem(ToolDescription tool, JsonObject input, JsonArray? suggestions, Action<JsonObject> answer, Action<string>? onModeChanged)
        : base(answer)
    {
        Tool = tool;
        _input = input;
        _suggestions = suggestions;
        _onModeChanged = onModeChanged;
        AlwaysLabel = suggestions == null ? null : SuggestionLabel(suggestions);
    }

    public ToolDescription Tool { get; }

    /// <summary>The "don't ask again" choice Claude Code offers for this request, if any.</summary>
    public string? AlwaysLabel { get; }

    [ObservableProperty] private string _feedback = string.Empty;

    [RelayCommand]
    private void Allow() => Reply(PermissionAnswer.Allow(_input), "Allowed");

    [RelayCommand]
    private void AllowAlways()
    {
        Reply(PermissionAnswer.Allow(_input, _suggestions), AlwaysLabel ?? "Allowed");
        if (_suggestions?.OfType<JsonObject>().FirstOrDefault(s => (string?)s["type"] == "setMode") is { } setMode &&
            (string?)setMode["mode"] is { } mode)
            _onModeChanged?.Invoke(mode);
    }

    [RelayCommand]
    private void Deny() =>
        Reply(PermissionAnswer.Deny(Feedback.Trim().Length > 0
            ? $"The user declined this. Their note: {Feedback.Trim()}"
            : "The user declined this tool use."), "Denied");

    private static string? SuggestionLabel(JsonArray suggestions)
    {
        if (suggestions.OfType<JsonObject>().FirstOrDefault() is not { } first) return null;
        var scope = (string?)first["destination"] switch
        {
            "session" => "for this chat",
            "localSettings" or "projectSettings" => "in this project",
            "userSettings" => "everywhere",
            _ => null,
        };
        switch ((string?)first["type"])
        {
            case "setMode" when (string?)first["mode"] == "acceptEdits":
                return "Allow all edits " + (scope ?? "for this chat");
            case "addRules" when first["rules"] is JsonArray rules && rules.OfType<JsonObject>().FirstOrDefault() is { } rule:
                var what = (string?)rule["ruleContent"] is { Length: > 0 } content ? $"{rule["toolName"]}({content})" : (string?)rule["toolName"];
                return $"Always allow {what}" + (scope != null ? " " + scope : string.Empty);
            case "addDirectories":
                return "Allow this folder " + (scope ?? "for this chat");
            default:
                return "Don't ask again " + (scope ?? "for this chat");
        }
    }
}

/// <summary>Claude's AskUserQuestion tool: one or more questions, each with options and room for another answer.</summary>
public sealed partial class QuestionChatItem : PromptChatItem
{
    private readonly JsonObject _input;

    public QuestionChatItem(JsonObject input, Action<JsonObject> answer) : base(answer)
    {
        _input = input;
        Questions = (input["questions"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(q => new QuestionViewModel(
                (string?)q["question"] ?? string.Empty,
                (string?)q["header"],
                q["multiSelect"]?.GetValue<bool>() == true,
                (q["options"] as JsonArray ?? []).OfType<JsonObject>()
                    .Select(o => new QuestionOptionViewModel((string?)o["label"] ?? string.Empty, (string?)o["description"]))
                    .ToList()))
            .ToList();

        // Submit waits until every question has an answer.
        foreach (var question in Questions)
        {
            question.PropertyChanged += (_, _) => SubmitCommand.NotifyCanExecuteChanged();
            foreach (var option in question.Options) option.PropertyChanged += (_, _) => SubmitCommand.NotifyCanExecuteChanged();
        }
    }

    public IReadOnlyList<QuestionViewModel> Questions { get; }

    private bool CanSubmit() => IsPending && Questions.All(q => q.IsAnswered);

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private void Submit()
    {
        var answers = new JsonObject();
        foreach (var question in Questions)
        {
            var picked = question.Options.Where(o => o.IsSelected).Select(o => o.Label).ToList();
            if (question.Other.Trim().Length > 0) picked.Add(question.Other.Trim());
            answers[question.Question] = string.Join(", ", picked);
        }
        var input = (JsonObject)_input.DeepClone();
        input["answers"] = answers;
        Reply(PermissionAnswer.Allow(input), "Answered");
    }

    [RelayCommand]
    private void Skip() => Reply(PermissionAnswer.Deny("The user skipped the question."), "Skipped");
}

public sealed partial class QuestionViewModel(string question, string? header, bool multiSelect, IReadOnlyList<QuestionOptionViewModel> options) : ObservableObject
{
    public string Question { get; } = question;
    public string? Header { get; } = header;
    public bool MultiSelect { get; } = multiSelect;
    public IReadOnlyList<QuestionOptionViewModel> Options { get; } = options;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnswered))]
    private string _other = string.Empty;

    public bool IsAnswered => Options.Any(o => o.IsSelected) || Other.Trim().Length > 0;

    // Single choice: picking one clears the rest.
    [RelayCommand]
    private void Pick(QuestionOptionViewModel option)
    {
        if (MultiSelect)
        {
            option.IsSelected = !option.IsSelected;
            return;
        }
        foreach (var o in Options) o.IsSelected = o == option;
    }
}

public sealed partial class QuestionOptionViewModel(string label, string? description) : ObservableObject
{
    public string Label { get; } = label;
    public string? Description { get; } = description;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>Claude's plan, from plan mode: approve it to let Claude start, or keep planning.</summary>
public sealed partial class PlanChatItem : PromptChatItem
{
    private readonly JsonObject _input;
    private readonly Action<string> _onModeChanged;

    public PlanChatItem(JsonObject input, Action<JsonObject> answer, Action<string> onModeChanged) : base(answer)
    {
        _input = input;
        _onModeChanged = onModeChanged;
        Plan = (string?)input["plan"] ?? string.Empty;
    }

    public string Plan { get; }
    [ObservableProperty] private string _feedback = string.Empty;

    [RelayCommand]
    private void ApproveAcceptEdits() => Approve("acceptEdits", "Approved: accepting edits");

    [RelayCommand]
    private void ApproveAsk() => Approve("default", "Approved: asking before edits");

    [RelayCommand]
    private void KeepPlanning() =>
        Reply(PermissionAnswer.Deny(Feedback.Trim().Length > 0
            ? $"The user wants to keep planning. Their feedback: {Feedback.Trim()}"
            : "The user wants to keep planning."), "Kept planning");

    private void Approve(string mode, string outcome)
    {
        var setMode = new JsonArray(new JsonObject { ["type"] = "setMode", ["mode"] = mode, ["destination"] = "session" });
        Reply(PermissionAnswer.Allow(_input, setMode), outcome);
        _onModeChanged(mode == "default" ? AgentOptions.ClaudeAskMode : mode);
    }
}

internal static class ClipboardText
{
    public static void Set(string text)
    {
        try { System.Windows.Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.COMException) { } // another app has the clipboard open
    }
}

internal static class JsonInput
{
    public static JsonElement ToElement(JsonObject input) => JsonSerializer.SerializeToElement(input);
}
