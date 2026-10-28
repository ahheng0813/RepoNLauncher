using System.IO;

namespace RepoNLauncher.Services;

/// <summary>Where Claude Code and Codex keep their files on this PC.</summary>
public static class AgentPaths
{
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string? ClaudeCustomDir => Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir ? dir : null;

    /// <summary>~/.claude, or CLAUDE_CONFIG_DIR, which moves everything.</summary>
    public static string ClaudeConfigDir => ClaudeCustomDir ?? Path.Combine(Home, ".claude");

    /// <summary>.claude.json sits in the home folder, unless CLAUDE_CONFIG_DIR moves it too.</summary>
    public static string ClaudeStatePath => Path.Combine(ClaudeCustomDir ?? Home, ".claude.json");

    /// <summary>Session transcripts, one folder per working directory.</summary>
    public static string ClaudeProjectsDir => Path.Combine(ClaudeConfigDir, "projects");

    /// <summary>~/.codex, or CODEX_HOME.</summary>
    public static string CodexHome => Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } dir ? dir : Path.Combine(Home, ".codex");

    /// <summary>Folders of Codex session logs (rollout-*.jsonl).</summary>
    public static IEnumerable<string> CodexSessionDirs => [Path.Combine(CodexHome, "sessions"), Path.Combine(CodexHome, "archived_sessions")];
}
