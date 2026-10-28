# Builds the installers with Velopack (vpk, pinned in .config\dotnet-tools.json) into release\out:
#   RepoNLauncher-win-Setup.exe   installs for the current user, no admin needed
#   RepoNLauncher-win.msi         install wizard, for the current user or everyone
#   RepoNLauncher-win-Portable.zip, and the packages installed copies update from
# Usage:  .\release.ps1                  build the <Version> in src\RepoNLauncher\RepoNLauncher.csproj
#         .\release.ps1 -Version 1.1.0   build another version (raise it for every release)
#         .\release.ps1 -Publish         also publish it as a GitHub release, which installed copies update to.
#                                        Needs the GitHub CLI signed in (gh auth login) and a public repo for others.
param([string]$Version, [switch]$Publish)

$ErrorActionPreference = 'Stop'
$root    = $PSScriptRoot
$proj    = Join-Path $root 'src\RepoNLauncher\RepoNLauncher.csproj'
$icon    = Join-Path $root 'src\RepoNLauncher\Assets\app.ico'
$appDir  = Join-Path $root 'release\app'
$outDir  = Join-Path $root 'release\out'
$repoUrl = 'https://github.com/ahheng0813/RepoNLauncher'

if (-not $Version) {
    $Version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
Write-Host "Releasing Repo'N'Launcher $Version"

Push-Location $root
try {
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed ($LASTEXITCODE)" }

    # A plain folder rather than publish.ps1's single exe: an update then only downloads the files that changed.
    # PublishDir is relative to the project: MSBuild quotes paths with ' internally, and this repo's folder name has one.
    if (Test-Path $appDir) { Remove-Item $appDir -Recurse -Force }
    dotnet publish $proj -c Release -r win-x64 --self-contained -p:PublishSingleFile=false -p:DebugType=none `
        -p:Version=$Version -p:PublishDir=..\..\release\app\
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

    if ($Publish) {
        $token = gh auth token
        if ($LASTEXITCODE -ne 0 -or -not $token) { throw 'Sign in to the GitHub CLI first: gh auth login' }
        # The newest published release lets vpk make a small delta update from it. The first release has none.
        dotnet vpk download github --repoUrl $repoUrl --token $token --outputDir $outDir
        if ($LASTEXITCODE -ne 0) { Write-Host 'No earlier release downloaded; this one ships as a full update only.' }
    }

    dotnet vpk pack --packId RepoNLauncher --packVersion $Version --runtime win-x64 --packDir $appDir --mainExe RepoNLauncher.exe `
        --packTitle "Repo'N'Launcher" --packAuthors AhHeng --icon $icon --msi --outputDir $outDir
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed ($LASTEXITCODE)" }

    if ($Publish) {
        dotnet vpk upload github --repoUrl $repoUrl --token $token --outputDir $outDir --publish `
            --tag "v$Version" --releaseName "Repo'N'Launcher $Version"
        if ($LASTEXITCODE -ne 0) { throw "vpk upload failed ($LASTEXITCODE)" }
        Write-Host "Published $repoUrl/releases/tag/v$Version"
    }
}
finally {
    Pop-Location
}

Get-ChildItem $outDir -Filter "RepoNLauncher-win*" | ForEach-Object { Write-Host ("{0}  {1:N1} MB" -f $_.Name, ($_.Length / 1MB)) }
