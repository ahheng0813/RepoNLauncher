using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;
using Velopack.Locators;

namespace RepoNLauncher.Services;

/// <summary>
/// Uninstalls the app and removes everything it keeps on this PC: the settings and GitHub sign-in, the scrcpy it
/// downloaded, the map and preview caches, its temp files, update packages and log, and the desktop icon. Projects' own
/// folders stay, and so does Git's own GitHub sign-in, which belongs to Git.
/// A running app can't delete itself, so a PowerShell script does it once the app has closed. It runs the installer's
/// own uninstall first (msiexec for the .msi, Update.exe for the Setup.exe) and deletes the rest only when that worked,
/// so saying no when Windows asks for approval leaves everything as it was.
/// </summary>
public static class Uninstaller
{
    // Its settings come in environment variables, which need no quoting however odd the paths.
    private const string Script = """
        $ErrorActionPreference = 'SilentlyContinue'
        function Tell([string]$text, [int]$icon) {
            # In front and on top: the app that asked has closed, so nothing else brings it forward.
            $null = (New-Object -ComObject WScript.Shell).Popup($text, 0, "Uninstall Repo'N'Launcher", $icon + 0x11000)
        }

        # The app closes once this has started; one still open after a minute is stopped.
        $app = Get-Process -Id $env:RNL_APP_PID
        if ($app -and -not $app.WaitForExit(60000)) { Stop-Process -Id $app.Id -Force; $null = $app.WaitForExit(5000) }

        if ($env:RNL_UNINSTALLER) {
            $run = Start-Process -FilePath $env:RNL_UNINSTALLER -ArgumentList $env:RNL_UNINSTALL_ARGS -Wait -PassThru
            # From msiexec, 1605 means it was uninstalled already, and 1641 and 3010 that Windows wants a restart to finish.
            if (-not $run -or $run.ExitCode -notin 0, 1605, 1641, 3010) {
                Tell "Repo'N'Launcher wasn't uninstalled, so nothing was removed. To try again, open it and click Uninstall." 48
                exit 1
            }
        }

        $left = @($env:RNL_REMOVE -split '\|' | Where-Object { $_ })
        # A program still running from one of them, like the adb server of the scrcpy the app downloaded, would keep it.
        Get-Process | Where-Object { $_.Id -ne $PID -and $_.Path } | Where-Object {
            $exe = $_.Path; $left | Where-Object { $exe.StartsWith($_.TrimEnd('\') + '\', 'OrdinalIgnoreCase') } } | Stop-Process -Force
        # Some let go a moment late, like the file preview's WebView2 after the app has closed.
        for ($try = 0; $try -lt 10; $try++) {
            if ($try) { Start-Sleep -Seconds 1 }
            $left | ForEach-Object { Remove-Item -LiteralPath $_ -Recurse -Force }
            $left = @($left | Where-Object { Test-Path -LiteralPath $_ })
            if (-not $left.Count) { break }
        }
        Get-ChildItem -LiteralPath $env:RNL_LOG_DIR -Filter $env:RNL_LOG_FILTER | Remove-Item -Force
        if ($env:RNL_REMOVE_IF_EMPTY -and -not (Get-ChildItem -LiteralPath $env:RNL_REMOVE_IF_EMPTY -Force)) {
            Remove-Item -LiteralPath $env:RNL_REMOVE_IF_EMPTY -Force
        }

        if ($left.Count) {
            Tell ("Repo'N'Launcher was uninstalled, but another program is using these, so they're still there. " +
                "Delete them once it's closed:`n`n" + ($left -join "`n")) 48
        } else {
            Tell "Repo'N'Launcher was uninstalled, along with everything it kept on this PC." 64
        }
        """;

    /// <summary>Starts the script, which waits for the app to close: close it straight after.</summary>
    public static void Start()
    {
        var locator = VelopackLocator.IsCurrentSet ? VelopackLocator.Current : null;
        var root = locator?.RootAppDir ?? throw new InvalidOperationException("Only an installed copy can uninstall itself.");
        var appId = locator.AppId ?? "RepoNLauncher";
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var remove = new List<string>
        {
            DashboardConfigService.AppDataDir, // settings, the GitHub sign-in, the downloaded scrcpy
            // Map tiles, the file preview's WebView2 data, and the update packages of a copy installed for everyone.
            Path.Combine(localAppData, Path.GetFileName(DashboardConfigService.AppDataDir)),
            Path.Combine(Path.GetTempPath(), "RepoNLauncher"), // pasted pictures, opened phone files, emulator logs
            Path.Combine(Path.GetTempPath(), "velopack_" + appId),
        };
        if (DesktopIcon.OwnLinkPath is { } icon) remove.Add(icon);

        string? uninstaller = null, uninstallArgs = null, removeIfEmpty = null;
        if (locator.IsPortable)
        {
            // Unzipped wherever its user chose, maybe among their own files, so Update.exe's uninstall, which empties
            // the whole folder, is no use: only what came in the zip goes, and the folder only if that leaves it empty.
            remove.AddRange(new[] { "current", "packages", "Update.exe", ".portable", StubFileName() }
                .OfType<string>().Select(name => Path.Combine(root, name)));
            removeIfEmpty = root;
        }
        else if (File.Exists(Path.Combine(root, ".msi-installed")))
        {
            // msiexec removes the install folder and Velopack's files in Local too, asking Windows' approval first when
            // it was installed for everyone.
            uninstaller = "msiexec.exe";
            uninstallArgs = $"/x {MsiProductCode(appId)} /passive /norestart";
        }
        else
        {
            uninstaller = locator.UpdateExePath ?? throw new InvalidOperationException("The app's Update.exe is missing.");
            uninstallArgs = "uninstall --silent";
        }

        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(), // not one of the folders it deletes
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
                     Convert.ToBase64String(Encoding.Unicode.GetBytes(Script)) })
            info.ArgumentList.Add(arg);
        info.Environment["RNL_APP_PID"] = Environment.ProcessId.ToString();
        info.Environment["RNL_UNINSTALLER"] = uninstaller;
        info.Environment["RNL_UNINSTALL_ARGS"] = uninstallArgs;
        info.Environment["RNL_REMOVE"] = string.Join("|", remove); // | can't be in a path
        info.Environment["RNL_REMOVE_IF_EMPTY"] = removeIfEmpty;
        info.Environment["RNL_LOG_DIR"] = Path.Combine(localAppData, "velopack");
        info.Environment["RNL_LOG_FILTER"] = $"velopack_{appId}.log*";
        using var _ = Process.Start(info);
    }

    // The .msi's entry in Windows' list of apps names the product code msiexec uninstalls it by.
    private static string MsiProductCode(string appId)
    {
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var key = hive.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\MSI:{appId}");
            if (key?.GetValue("UninstallString") is string command && Regex.Match(command, @"\{[0-9A-Fa-f-]{36}\}") is { Success: true } code)
                return code.Value;
        }
        throw new InvalidOperationException("Windows has no uninstall entry for it. Uninstall it from Settings > Apps instead.");
    }

    // Velopack names the launcher it puts beside current\ after the app's title.
    private static string? StubFileName()
    {
        try
        {
            var title = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "sq.version"))
                .Descendants().FirstOrDefault(e => e.Name.LocalName == "title")?.Value;
            return string.IsNullOrWhiteSpace(title) ? null : title + ".exe";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or XmlException)
        {
            return null;
        }
    }
}
