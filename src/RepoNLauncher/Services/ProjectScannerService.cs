using System.IO;
using RepoNLauncher.Models;

namespace RepoNLauncher.Services;

public interface IProjectScanner
{
    /// <summary>Walks a root folder (bounded depth) looking for git repos and classifies each one.</summary>
    List<ProjectInfo> Scan(string rootPath, int maxDepth = 3);

    ProjectKind DetectKind(string projectPath);
}

public class ProjectScannerService : IProjectScanner
{
    private static readonly string[] SkipDirNames =
    {
        ".git", "node_modules", "bin", "obj", "build", ".dart_tool", ".idea", ".vscode", ".vs"
    };

    private static readonly string[] PythonMarkers =
    {
        "pyproject.toml", "requirements.txt", "setup.py", "Pipfile", "manage.py"
    };

    public List<ProjectInfo> Scan(string rootPath, int maxDepth = 3)
    {
        var found = new List<ProjectInfo>();
        if (!Directory.Exists(rootPath)) return found;

        ScanRecursive(rootPath, 0, maxDepth, found);
        return found;
    }

    private void ScanRecursive(string dir, int depth, int maxDepth, List<ProjectInfo> found)
    {
        if (depth > maxDepth) return;

        if (Directory.Exists(Path.Combine(dir, ".git")))
        {
            found.Add(new ProjectInfo
            {
                Path = dir,
                Name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                Kind = DetectKind(dir),
                IsAutoDiscovered = true,
            });
            return; // don't descend into nested repos
        }

        IEnumerable<string> subDirs;
        try
        {
            subDirs = Directory.EnumerateDirectories(dir);
        }
        catch (UnauthorizedAccessException) { return; }
        catch (IOException) { return; }

        foreach (var sub in subDirs)
        {
            var name = Path.GetFileName(sub);
            if (SkipDirNames.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            ScanRecursive(sub, depth + 1, maxDepth, found);
        }
    }

    public ProjectKind DetectKind(string projectPath)
    {
        if (File.Exists(Path.Combine(projectPath, "pubspec.yaml")))
            return ProjectKind.Flutter;

        if (HasDotNetProjectFile(projectPath, depth: 0, maxDepth: 2))
            return ProjectKind.DotNet;

        if (File.Exists(Path.Combine(projectPath, "package.json")))
            return ProjectKind.Node;

        if (PythonMarkers.Any(file => File.Exists(Path.Combine(projectPath, file))))
            return ProjectKind.Python;

        return ProjectKind.Unknown;
    }

    // .slnx is the newer XML-based solution format (alongside the classic .sln); several repos here
    // also keep the solution/project files one folder below the git root, so this checks a couple
    // levels down too instead of only the exact repo root.
    private static bool HasDotNetProjectFile(string dir, int depth, int maxDepth)
    {
        if (Directory.EnumerateFiles(dir, "*.csproj", SearchOption.TopDirectoryOnly).Any() ||
            Directory.EnumerateFiles(dir, "*.sln", SearchOption.TopDirectoryOnly).Any() ||
            Directory.EnumerateFiles(dir, "*.slnx", SearchOption.TopDirectoryOnly).Any())
            return true;

        if (depth >= maxDepth) return false;

        IEnumerable<string> subDirs;
        try
        {
            subDirs = Directory.EnumerateDirectories(dir);
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }

        foreach (var sub in subDirs)
        {
            var name = Path.GetFileName(sub);
            if (SkipDirNames.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (HasDotNetProjectFile(sub, depth + 1, maxDepth)) return true;
        }

        return false;
    }
}
