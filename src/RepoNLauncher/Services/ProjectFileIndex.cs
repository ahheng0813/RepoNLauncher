using System.Diagnostics;
using System.IO;
using System.Text;

namespace RepoNLauncher.Services;

/// <summary>
/// The files of a project, for @-mentions in a chat prompt. A git repo lists what git tracks plus new files it doesn't
/// ignore; any other folder is walked, skipping build output and dependency folders. Lists are kept for a minute.
/// </summary>
public sealed class ProjectFileIndex
{
    private const int MaxFiles = 20_000;
    private static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(1);
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "build", ".dart_tool", ".gradle", ".idea", ".vs", "venv", ".venv", "__pycache__", "Pods", "dist",
    };

    private readonly Dictionary<string, (DateTime At, IReadOnlyList<string> Files)> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Paths relative to <paramref name="root"/>, with forward slashes.</summary>
    public async Task<IReadOnlyList<string>> GetAsync(string root)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(root, out var cached) && DateTime.UtcNow - cached.At < KeepFor) return cached.Files;
        }
        var files = await Task.Run(() => GitFiles(root) ?? WalkFiles(root));
        lock (_cache) _cache[root] = (DateTime.UtcNow, files);
        return files;
    }

    /// <summary>The best matches for what was typed after @: file names that start with it, then names and paths that contain it.</summary>
    public static IReadOnlyList<string> Match(IReadOnlyList<string> files, string query, int max = 50)
    {
        if (query.Length == 0) return files.Take(max).ToList();
        var q = query.Replace('\\', '/');
        return files
            .Select(f => (Path: f, Name: f[(f.LastIndexOf('/') + 1)..]))
            .Select(f => (f.Path, Rank: f.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0
                : f.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ? 1
                : f.Path.Contains(q, StringComparison.OrdinalIgnoreCase) ? 2 : 3))
            .Where(f => f.Rank < 3)
            .OrderBy(f => f.Rank).ThenBy(f => f.Path.Length)
            .Take(max)
            .Select(f => f.Path)
            .ToList();
    }

    private static IReadOnlyList<string>? GitFiles(string root)
    {
        if (!Directory.Exists(Path.Combine(root, ".git")) && !File.Exists(Path.Combine(root, ".git"))) return null;
        try
        {
            var info = new ProcessStartInfo("git")
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var arg in new[] { "-c", "core.quotepath=off", "ls-files", "--cached", "--others", "--exclude-standard" }) info.ArgumentList.Add(arg);
            using var git = Process.Start(info);
            if (git == null) return null;
            var files = new List<string>();
            // Read to the end even past the cap, or git blocks on a full pipe.
            while (git.StandardOutput.ReadLine() is { } line)
                if (line.Length > 0 && files.Count < MaxFiles) files.Add(line);
            if (!git.WaitForExit(10_000) || git.ExitCode != 0) return null;
            return files;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> WalkFiles(string root)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0 && files.Count < MaxFiles)
        {
            var dir = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                    files.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(sub);
                    if (!SkipDirs.Contains(name) && !name.StartsWith('.') && new DirectoryInfo(sub).LinkTarget == null) pending.Push(sub);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return files;
    }
}
