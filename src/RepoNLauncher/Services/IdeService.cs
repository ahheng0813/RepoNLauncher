using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace RepoNLauncher.Services;

public enum Ide
{
    VisualStudio,
    VisualStudioCode,
    AndroidStudio,
}

public interface IIdeService
{
    /// <summary>Full path of the IDE's executable, or null if it isn't installed.</summary>
    string? FindExecutable(Ide ide);

    /// <summary>Starts the IDE on a folder or a file such as a solution.</summary>
    void Open(string executable, string target);

    /// <summary>Starts a user-added program with its own argument line, in the given folder.</summary>
    void OpenWith(string program, string arguments, string workingDirectory);
}

public class IdeService : IIdeService
{
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    public string? FindExecutable(Ide ide) => ide switch
    {
        Ide.VisualStudio => FindVisualStudio(),
        Ide.VisualStudioCode => FindVsCode(),
        Ide.AndroidStudio => FindAndroidStudio(),
        _ => null,
    };

    public void Open(string executable, string target)
    {
        // Shell-execute, like a Start-menu launch: the IDE inherits none of the dashboard's handles.
        var info = new ProcessStartInfo(executable) { UseShellExecute = true };
        info.ArgumentList.Add(target);
        using var _ = Process.Start(info);
    }

    public void OpenWith(string program, string arguments, string workingDirectory)
    {
        // Shell-execute too, so a bare name like wt.exe resolves through App Paths and PATH the way the Run dialog does;
        // the working folder matters for terminals and shells started without a path argument.
        var info = new ProcessStartInfo(program, arguments) { UseShellExecute = true, WorkingDirectory = workingDirectory };
        using var _ = Process.Start(info);
    }

    private static string? FindVisualStudio()
    {
        // vswhere ships with the Visual Studio installer and knows every instance; -latest picks the newest.
        var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (File.Exists(vswhere))
        {
            var info = new ProcessStartInfo(vswhere)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var arg in new[] { "-latest", "-prerelease", "-utf8", "-property", "productPath" })
                info.ArgumentList.Add(arg);

            try
            {
                using var process = Process.Start(info)!;
                var devenv = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                if (File.Exists(devenv)) return devenv;
            }
            catch (Win32Exception) { }
        }

        return FromAppPaths("devenv.exe");
    }

    private static string? FindVsCode() =>
        FromAppPaths("code.exe")
        ?? FirstExisting(
            Path.Combine(LocalAppData, "Programs", "Microsoft VS Code", "Code.exe"), // user installer
            Path.Combine(ProgramFiles, "Microsoft VS Code", "Code.exe"));         // system installer

    private static string? FindAndroidStudio()
    {
        // The Android Studio installer records its folder here; JetBrains Toolbox installs under %LocalAppData%\Programs.
        string? registered;
        using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Android Studio"))
            registered = key?.GetValue("Path") as string;

        var installDirs = new[]
        {
            registered,
            Path.Combine(ProgramFiles, "Android", "Android Studio"),
            Path.Combine(LocalAppData, "Programs", "Android Studio"),
        };

        // The launcher is studio64.exe; studio.exe is checked too in case a release drops the suffix.
        return FirstExisting(installDirs
            .Where(dir => !string.IsNullOrWhiteSpace(dir))
            .SelectMany(dir => new[] { Path.Combine(dir!, "bin", "studio64.exe"), Path.Combine(dir!, "bin", "studio.exe") })
            .ToArray());
    }

    // Installers register their exe under App Paths so the Run dialog can start it without it being on PATH.
    private static string? FromAppPaths(string exeName)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exeName}");
            var path = (key?.GetValue(null) as string)?.Trim('"');
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static string? FirstExisting(params string[] paths) => paths.FirstOrDefault(File.Exists);
}
