using System.IO;
using RepoNLauncher.Models;

namespace RepoNLauncher.Services;

/// <summary>Turns a script or program picked in the Run tab into the command that runs it from the project folder.</summary>
public static class StartCommandBuilder
{
    private const string Python = "python";

    // Runner per file type. Types not listed run as they are (.bat, .cmd, .exe); for anything else
    // cmd.exe falls back to the program Windows associates with the file.
    private static readonly Dictionary<string, string> Runners = new(StringComparer.OrdinalIgnoreCase)
    {
        [".py"] = Python, [".pyw"] = Python,
        [".js"] = "node", [".mjs"] = "node", [".cjs"] = "node",
        [".ts"] = "npx tsx", [".mts"] = "npx tsx", [".cts"] = "npx tsx",
        [".ps1"] = "powershell -NoProfile -File",
        [".sh"] = "bash",
        [".rb"] = "ruby",
        [".php"] = "php",
        [".go"] = "go run",
        [".dart"] = "dart run",
        [".java"] = "java",
    };

    // Virtual-environment folders a Python project commonly keeps; its interpreter has the project's packages installed.
    private static readonly string[] VenvDirs = { ".venv", "venv", "env" };

    public static string Build(string projectPath, string file)
    {
        // The command runs in the project folder, so a file inside it is named relative to it.
        var relative = Path.GetRelativePath(projectPath, file);
        var target = Quote(relative.StartsWith("..") || Path.IsPathRooted(relative) ? file : relative);

        if (!Runners.TryGetValue(Path.GetExtension(file), out var runner)) return target;
        if (runner == Python) runner = Quote(PythonFor(projectPath));
        return $"{runner} {target}";
    }

    /// <summary>The project kind a start file implies, or Unknown when it says nothing.</summary>
    public static ProjectKind KindFor(string file) =>
        Runners.TryGetValue(Path.GetExtension(file), out var runner) switch
        {
            true when runner == Python => ProjectKind.Python,
            true when runner is "node" or "npx tsx" => ProjectKind.Node,
            _ => ProjectKind.Unknown,
        };

    private static string PythonFor(string projectPath)
    {
        foreach (var dir in VenvDirs)
        {
            var venvPython = Path.Combine(dir, "Scripts", "python.exe");
            if (File.Exists(Path.Combine(projectPath, venvPython))) return venvPython;
        }
        return Python;
    }

    // cmd.exe splits on spaces and gives & ( ) ^ and others special meaning, so quote any path using them.
    private static string Quote(string path) =>
        path.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or '\\' or '/' or ':') ? path : $"\"{path}\"";
}
