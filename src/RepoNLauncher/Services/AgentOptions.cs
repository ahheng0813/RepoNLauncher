using System.IO;
using System.Text.Json;
using RepoNLauncher.Models;

namespace RepoNLauncher.Services;

/// <summary>The choices offered for each agent's model, effort and permissions.</summary>
public static class AgentOptions
{
    // Aliases rather than versions, so the newest model of each family is used without updating this list.
    public static readonly IReadOnlyList<AgentOption> ClaudeModels =
    [
        new(string.Empty, "Default", "Your plan's default model"),
        new("fable", "Fable", "Claude Fable"),
        new("opus", "Opus", "Claude Opus, for complex work"),
        new("sonnet", "Sonnet", "Claude Sonnet, balanced"),
        new("haiku", "Haiku", "Claude Haiku, fastest"),
    ];

    public static readonly IReadOnlyList<AgentOption> ClaudeEfforts =
    [
        new(string.Empty, "Default effort"),
        new("low", "Low effort"),
        new("medium", "Medium effort"),
        new("high", "High effort"),
        new("xhigh", "Extra high effort"),
        new("max", "Max effort"),
    ];

    public const string ClaudeAskMode = "manual";

    public static readonly IReadOnlyList<AgentOption> ClaudeModes =
    [
        new(ClaudeAskMode, "Ask before edits", "Claude asks here before editing files or running commands"),
        new("acceptEdits", "Accept edits", "Edits files without asking; still asks before other commands"),
        new("plan", "Plan mode", "Looks around and proposes a plan; changes nothing until you approve it"),
        new("auto", "Auto", "Claude Code's safety check decides what needs your OK"),
        new("bypassPermissions", "Bypass permissions", "Never asks. Only for folders it may change freely"),
    ];

    public static readonly IReadOnlyList<AgentOption> CodexModes =
    [
        new("read-only", "Read only", "Codex can read files and run read-only commands"),
        new("workspace-write", "Edit project", "Codex can edit files in the project folder"),
        new("danger-full-access", "Full access", "No sandbox: Codex can change anything on this PC"),
    ];

    public const string CodexDefaultMode = "workspace-write";

    private static readonly AgentOption DefaultEffort = new(string.Empty, "Default effort");

    /// <summary>Codex's models, from the list the Codex app and CLI keep (~/.codex/models_cache.json), each with its effort levels.</summary>
    public static IReadOnlyList<CodexModel> CodexModels()
    {
        var models = new List<CodexModel> { new(new AgentOption(string.Empty, "Default", "Codex's default model"), StandardEfforts()) };
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AgentPaths.CodexHome, "models_cache.json")));
            if (!document.RootElement.TryGetProperty("models", out var list) || list.ValueKind != JsonValueKind.Array) return models;

            foreach (var model in list.EnumerateArray()
                         .Where(m => Str(m, "visibility") == "list" && Str(m, "slug") != null)
                         .OrderBy(m => m.TryGetProperty("priority", out var p) && p.TryGetInt32(out var n) ? n : int.MaxValue))
            {
                var efforts = new List<AgentOption> { DefaultEffort };
                if (model.TryGetProperty("supported_reasoning_levels", out var levels) && levels.ValueKind == JsonValueKind.Array)
                    efforts.AddRange(levels.EnumerateArray()
                        .Where(l => Str(l, "effort") != null)
                        .Select(l => new AgentOption(Str(l, "effort")!, EffortLabel(Str(l, "effort")!), Str(l, "description"))));
                models.Add(new CodexModel(new AgentOption(Str(model, "slug")!, Str(model, "display_name") ?? Str(model, "slug")!, Str(model, "description")),
                    efforts.Count > 1 ? efforts : StandardEfforts()));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return models;
    }

    private static List<AgentOption> StandardEfforts() =>
        [DefaultEffort, .. new[] { "low", "medium", "high", "xhigh" }.Select(e => new AgentOption(e, EffortLabel(e)))];

    private static string EffortLabel(string effort) => effort switch
    {
        "xhigh" => "Extra high effort",
        "minimal" => "Minimal effort",
        _ => char.ToUpperInvariant(effort[0]) + effort[1..] + " effort",
    };

    private static string? Str(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

public record CodexModel(AgentOption Option, IReadOnlyList<AgentOption> Efforts);
