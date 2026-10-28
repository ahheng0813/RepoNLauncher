using System.IO;
using System.Text.RegularExpressions;

namespace RepoNLauncher.Services;

/// <summary>
/// Which GitHub repository a local repo points at, read straight from its git config, so checking hundreds of repos
/// needs no git process each.
/// </summary>
public static class GitHubRemote
{
    private static readonly Regex SectionHeader = new(@"^\[\s*(?:remote\s+""(?<remote>[^""]*)""|[^\]]*)\s*\]", RegexOptions.IgnoreCase);
    private static readonly Regex UrlForm = new(@"^(?<scheme>https?|git|ssh)://(?:[^@/]+@)?(?<host>[^/:]+)(?::\d+)?/(?<owner>[^/]+)/(?<name>[^/]+?)(?:\.git)?/?$", RegexOptions.IgnoreCase);
    private static readonly Regex ScpForm = new(@"^(?:[^@/]+@)?(?<host>[^:/]+):(?<owner>[^/]+)/(?<name>[^/]+?)(?:\.git)?/?$");
    private static readonly Regex SshConfigLine = new(@"^\s*(?<key>\w+)\s*(?:=|\s)\s*(?<value>.+?)\s*$");

    private static readonly HashSet<string> GitHubHosts = new(StringComparer.OrdinalIgnoreCase) { "github.com", "www.github.com", "ssh.github.com" };

    // People with several GitHub accounts give each its own "Host github-work" + "HostName github.com" entry in
    // ~/.ssh/config, so their remotes say git@github-work:owner/repo.
    private static readonly Lazy<Dictionary<string, string>> SshAliases = new(ReadSshAliases);

    /// <summary>The GitHub owner and name of the repo's origin (or else its first GitHub remote), or null when it has none.</summary>
    public static (string Owner, string Name)? Find(string repoPath)
    {
        var configPath = ConfigPath(repoPath);
        if (configPath == null) return null;

        var urls = new List<(string Remote, string Url)>();
        try
        {
            string? remote = null;
            foreach (var raw in File.ReadLines(configPath))
            {
                var line = raw.Trim();
                var section = SectionHeader.Match(line);
                if (section.Success)
                {
                    remote = section.Groups["remote"].Success ? section.Groups["remote"].Value : null;
                    continue;
                }

                var equals = line.IndexOf('=');
                if (remote != null && equals > 0 && line[..equals].Trim().Equals("url", StringComparison.OrdinalIgnoreCase))
                    urls.Add((remote, line[(equals + 1)..].Trim().Trim('"')));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }

        // In a fork, origin is the copy this clone works on and upstream the original, so origin decides.
        return urls
            .OrderBy(u => u.Remote == "origin" ? 0 : 1)
            .Select(u => Parse(u.Url))
            .FirstOrDefault(parsed => parsed != null);
    }

    public static (string Owner, string Name)? Parse(string url)
    {
        var match = UrlForm.Match(url);
        var viaSsh = match.Success && match.Groups["scheme"].Value.Equals("ssh", StringComparison.OrdinalIgnoreCase);
        if (!match.Success)
        {
            match = ScpForm.Match(url);
            viaSsh = true;
        }
        if (!match.Success) return null;

        var host = match.Groups["host"].Value;
        var isGitHub = GitHubHosts.Contains(host) ||
                       (viaSsh && SshAliases.Value.TryGetValue(host, out var realHost) && GitHubHosts.Contains(realHost));
        return isGitHub ? (match.Groups["owner"].Value, match.Groups["name"].Value) : null;
    }

    private static string? ConfigPath(string repoPath)
    {
        var dotGit = Path.Combine(repoPath, ".git");
        if (Directory.Exists(dotGit)) return Path.Combine(dotGit, "config");

        // A worktree or submodule has a .git file pointing at its real git folder; a worktree shares the main repo's
        // config, which its "commondir" file leads to.
        try
        {
            var line = File.ReadLines(dotGit).FirstOrDefault() ?? string.Empty;
            if (!line.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase)) return null;

            var gitDir = Path.GetFullPath(Path.Combine(repoPath, line["gitdir:".Length..].Trim()));
            var commonDirFile = Path.Combine(gitDir, "commondir");
            if (File.Exists(commonDirFile))
                gitDir = Path.GetFullPath(Path.Combine(gitDir, File.ReadAllText(commonDirFile).Trim()));
            return Path.Combine(gitDir, "config");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static Dictionary<string, string> ReadSshAliases()
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");
        try
        {
            string[] hosts = [];
            foreach (var raw in File.ReadLines(path))
            {
                var match = SshConfigLine.Match(raw);
                if (!match.Success) continue;

                var value = match.Groups["value"].Value;
                if (match.Groups["key"].Value.Equals("Host", StringComparison.OrdinalIgnoreCase))
                    hosts = value.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                else if (match.Groups["key"].Value.Equals("HostName", StringComparison.OrdinalIgnoreCase))
                    foreach (var host in hosts) aliases.TryAdd(host, value);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return aliases;
    }
}
