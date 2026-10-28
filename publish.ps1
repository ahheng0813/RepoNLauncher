# Builds publish\RepoNLauncher.exe (single self-contained file), puts a shortcut on the Desktop and starts it.
# A running copy of the published exe is closed first, since Windows locks a running exe.
# Usage:  .\publish.ps1            close + build + shortcut + start
#         .\publish.ps1 -NoShortcut -NoLaunch
param([switch]$NoShortcut, [switch]$NoLaunch)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = Join-Path $root 'src\RepoNLauncher\RepoNLauncher.csproj'
$exe  = Join-Path $root 'publish\RepoNLauncher.exe'

# Only the published copy locks the exe; a Debug build running from bin\ is left alone.
foreach ($p in @(Get-Process RepoNLauncher -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })) {
    Write-Host "Closing Repo'N'Launcher (PID $($p.Id)) so the exe can be replaced..."
    # Close politely so it stops the phone mirror itself. Repeat because when the phone screen is popped out,
    # the first close may hit that window, which only docks the screen back.
    for ($i = 0; $i -lt 3 -and -not $p.HasExited; $i++) {
        $p.Refresh()
        $null = $p.CloseMainWindow()
        $null = $p.WaitForExit(4000)
    }
    if (-not $p.HasExited) {
        Stop-Process -Id $p.Id -Force
        $p.WaitForExit()
    }
}

dotnet publish $proj -p:PublishProfile=SingleExe
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

if (-not $NoShortcut) {
    $lnk = Join-Path ([Environment]::GetFolderPath('Desktop')) "Repo'N'Launcher.lnk"
    $sc = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk)
    $sc.TargetPath = $exe
    $sc.WorkingDirectory = Split-Path $exe
    $sc.IconLocation = "$exe,0"
    $sc.Save()
    Write-Host "Shortcut: $lnk"
}

Write-Host "Done: $exe ($([math]::Round((Get-Item $exe).Length / 1MB, 1)) MB)"

if (-not $NoLaunch) {
    Start-Process $exe -WorkingDirectory (Split-Path $exe)
    Write-Host "Started Repo'N'Launcher."
}
