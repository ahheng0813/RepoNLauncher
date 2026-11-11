using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RepoNLauncher.Services;

/// <summary>
/// The model and effort Claude Code runs with when a chat leaves them to it, from its settings files: the project's
/// .claude/settings.local.json and .claude/settings.json over the user's ~/.claude/settings.json, as Claude Code reads
/// them. Null where none of them says.
/// </summary>
public static class ClaudeSettings
{
    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>The model the settings pick (model: "opus"), or null.</summary>
    public static string? Model(string workingDirectory) =>
        Files(workingDirectory).Select(s => Str(s, "model")).FirstOrDefault(m => m != null);

    /// <summary>
    /// The effort the settings give a model: its own, under modelSettings, else the general effortLevel. An empty or
    /// null <paramref name="model"/> is Default's, the model the settings pick.
    /// </summary>
    public static string? Effort(string workingDirectory, string? model)
    {
        var files = Files(workingDirectory).ToList();
        if (string.IsNullOrEmpty(model)) model = files.Select(s => Str(s, "model")).FirstOrDefault(m => m != null);

        if (model != null)
        {
            foreach (var settings in files)
            {
                if (!settings.TryGetProperty("modelSettings", out var perModel) || perModel.ValueKind != JsonValueKind.Object) continue;
                var own = perModel.EnumerateObject()
                    .Where(p => Matches(p.Name, model))
                    .OrderByDescending(p => VersionOf(p.Name))
                    .Select(p => Str(p.Value, "effortLevel"))
                    .FirstOrDefault(e => e != null);
                if (own != null) return own;
            }
        }
        return files.Select(s => Str(s, "effortLevel")).FirstOrDefault(e => e != null);
    }

    /// <summary>The family a model setting names: opus for "opus", "opus[1m]" or "claude-opus-5-5".</summary>
    public static string Alias(string model)
    {
        var name = Bare(model);
        var family = Regex.Match(name, @"^claude-([a-z]+)-");
        return family.Success ? family.Groups[1].Value : name;
    }

    // modelSettings is keyed by model id (claude-opus-5-5); the model may be one (claude-opus-5-5) or an alias (opus).
    private static bool Matches(string id, string model)
    {
        var bareId = Bare(id);
        var bareModel = Bare(model);
        return bareId.Equals(bareModel, StringComparison.OrdinalIgnoreCase)
               || (!bareModel.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)
                   && Alias(bareId).Equals(bareModel, StringComparison.OrdinalIgnoreCase));
    }

    // "opus[1m]" is opus with a longer context.
    private static string Bare(string model) => Regex.Replace(model.Trim(), @"\[.*\]$", string.Empty).ToLowerInvariant();

    // The newest of several ids of one family: claude-opus-5-5 over claude-opus-4-8.
    private static string VersionOf(string id) =>
        string.Concat(Regex.Matches(id, @"\d+").Select(m => m.Value.PadLeft(6, '0')));

    // Highest precedence first.
    private static IEnumerable<JsonElement> Files(string workingDirectory)
    {
        var paths = new List<string>();
        if (Directory.Exists(workingDirectory))
        {
            paths.Add(Path.Combine(workingDirectory, ".claude", "settings.local.json"));
            paths.Add(Path.Combine(workingDirectory, ".claude", "settings.json"));
        }
        paths.Add(Path.Combine(AgentPaths.ClaudeConfigDir, "settings.json"));

        foreach (var path in paths)
        {
            JsonElement? root = null;
            try
            {
                if (File.Exists(path))
                {
                    using var document = JsonDocument.Parse(File.ReadAllBytes(path), Lenient);
                    if (document.RootElement.ValueKind == JsonValueKind.Object) root = document.RootElement.Clone();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            if (root is { } settings) yield return settings;
        }
    }

    private static string? Str(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : null;
}
