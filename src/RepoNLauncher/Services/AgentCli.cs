using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace RepoNLauncher.Services;

/// <summary>
/// Finds the Claude Code and Codex programs on this PC. Nothing is cached: a lookup is a few file checks, and the user
/// may install either tool while the dashboard is open.
/// </summary>
public static class AgentCli
{
    public const string ClaudeInstallUrl = "https://code.claude.com/docs/en/setup";
    public const string CodexInstallUrl = "https://developers.openai.com/codex/cli";

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string RoamingAppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>The claude program: on PATH, or where the native installer or npm puts it.</summary>
    public static string? FindClaude() =>
        OnPath("claude") ?? FirstExisting(
            Path.Combine(Home, ".local", "bin", "claude.exe"),
            Path.Combine(RoamingAppData, "npm", "claude.cmd"));

    /// <summary>The codex program: on PATH, from npm, or the copy the Codex app ships with.</summary>
    public static string? FindCodex() =>
        OnPath("codex") ?? FirstExisting(Path.Combine(RoamingAppData, "npm", "codex.cmd")) ?? CodexAppBinary();

    /// <summary>Starts <paramref name="program"/> with redirected UTF-8 streams. npm installs are .cmd shims, which only cmd can run.</summary>
    public static ProcessStartInfo StartInfo(string program, IEnumerable<string> arguments, string workingDirectory)
    {
        var args = arguments.ToList();
        ProcessStartInfo info;
        if (program.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || program.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            // /s: cmd strips just the outer quotes, leaving each quoted argument intact.
            var line = string.Join(' ', args.Prepend(program).Select(Quote));
            info = new ProcessStartInfo("cmd.exe", $"/d /s /c \"{line}\"");
        }
        else
        {
            info = new ProcessStartInfo(program);
            foreach (var arg in args) info.ArgumentList.Add(arg);
        }

        var utf8 = new UTF8Encoding(false);
        info.WorkingDirectory = workingDirectory;
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.StandardInputEncoding = utf8;
        info.StandardOutputEncoding = utf8;
        info.StandardErrorEncoding = utf8;
        return info;
    }

    /// <summary>Runs <paramref name="program"/> to the end and returns what it printed; null if it couldn't start, failed or ran past <paramref name="timeout"/>.</summary>
    public static string? Run(string program, IEnumerable<string> arguments, string workingDirectory, TimeSpan timeout)
    {
        try
        {
            using var process = Process.Start(StartInfo(program, arguments, workingDirectory));
            if (process == null) return null;
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync(); // drained, or a chatty program stalls on a full pipe
            if (!process.WaitForExit(timeout))
            {
                Kill(process);
                return null;
            }
            return process.ExitCode == 0 ? output.Result : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    public static void Kill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    /// <summary>A command line that runs <paramref name="program"/> in a console window, e.g. for signing in.</summary>
    public static string ConsoleCommand(string program, string arguments) => $"{Quote(program)} {arguments}";

    private static string Quote(string arg) =>
        arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"', '&', '|', '<', '>', '^', '\'']) < 0 ? arg : $"\"{arg.Replace("\"", "\\\"")}\"";

    private static string? OnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in new[] { ".exe", ".cmd", ".bat" })
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim('"'), name + extension);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
            }
        }
        return null;
    }

    private static string? FirstExisting(params string[] paths) => paths.FirstOrDefault(File.Exists);

    // The Codex app is a Microsoft Store package. Its folder under WindowsApps can't be listed without admin rights,
    // but Windows records each installed package's folder under the user's registry.
    private static string? CodexAppBinary()
    {
        try
        {
            using var packages = Registry.CurrentUser.OpenSubKey(
                @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            if (packages == null) return null;

            return packages.GetSubKeyNames()
                .Where(name => name.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase))
                .Select(name =>
                {
                    using var package = packages.OpenSubKey(name);
                    var root = package?.GetValue("PackageRootFolder") as string;
                    // OpenAI.Codex_26.1021.3705.0_x64__2p2nqsd0c76g0: the newest version wins if more than one is left behind.
                    var version = Version.TryParse(name.Split('_')[1], out var v) ? v : new Version();
                    return (Path: root == null ? null : Path.Combine(root, "app", "resources", "codex.exe"), Version: version);
                })
                .Where(p => p.Path != null && File.Exists(p.Path))
                .OrderByDescending(p => p.Version)
                .Select(p => p.Path)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
