# Builds the installers with Velopack (vpk, pinned in .config\dotnet-tools.json) into release\out:
#   RepoNLauncher-win-Setup.exe   installs for the current user, no admin needed
#   RepoNLauncher-win.msi         install wizard, for the current user or everyone
#   RepoNLauncher-win-Portable.zip, and the packages installed copies update from
# Usage:  .\release.ps1                  build the <Version> in src\RepoNLauncher\RepoNLauncher.csproj
#         .\release.ps1 -Version 1.1.0   build another version (raise it for every release)
#         .\release.ps1 -Publish         also publish it as a GitHub release, which installed copies update to.
#                                        Needs the GitHub CLI signed in (gh auth login) and a public repo for others.
#         .\release.ps1 -Bump patch      raise the version (patch 1.0.0 -> 1.0.1, minor -> 1.1.0, major -> 2.0.0), commit
#                                        and push just that, and stop. Add -Publish to release it straight after.
# Nothing here asks a question, so it also runs as a saved start command in the dashboard's Run tab.
param([string]$Version, [ValidateSet('patch', 'minor', 'major')][string]$Bump, [switch]$Publish)

$ErrorActionPreference = 'Stop'
$root    = $PSScriptRoot
$proj    = Join-Path $root 'src\RepoNLauncher\RepoNLauncher.csproj'
$icon    = Join-Path $root 'src\RepoNLauncher\Assets\app.ico'
$appDir  = Join-Path $root 'release\app'
$outDir  = Join-Path $root 'release\out'
$repoUrl = 'https://github.com/ahheng0813/RepoNLauncher'

# Raises <Version>, commits just that and pushes it, so the release is built from, and tagged at, what's on GitHub.
if ($Bump) {
    if ($Version) { throw 'Use -Bump or -Version, not both.' }
    Push-Location $root
    try {
        $uncommitted = git status --porcelain
        if ($uncommitted) { throw "Commit or stash these first, so the release matches what's on GitHub:`n$($uncommitted -join "`n")" }

        $default = (git symbolic-ref --short refs/remotes/origin/HEAD) -replace '^origin/', ''
        if ((git rev-parse --abbrev-ref HEAD) -ne $default) { throw "Switch to $default first: the release is tagged on $default." }

        git fetch --quiet origin
        if ($LASTEXITCODE -ne 0) { throw "git fetch failed ($LASTEXITCODE)" }
        $behind = [int](git rev-list --count "HEAD..origin/$default")
        if ($behind -gt 0) { throw "GitHub has $behind commit(s) this folder doesn't. Run git pull first." }

        if ($Publish) {
            # Before raising the version, so a missing sign-in doesn't leave a version without its release.
            $null = gh auth token
            if ($LASTEXITCODE -ne 0) { throw 'Sign in to the GitHub CLI first: gh auth login' }
        }

        $bytes = [IO.File]::ReadAllBytes($proj)
        $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
        $text = [IO.File]::ReadAllText($proj)
        $match = [regex]::Match($text, '<Version>(\d+)\.(\d+)\.(\d+)</Version>')
        if (-not $match.Success) { throw "There's no <Version>x.y.z</Version> in $proj." }
        $major, $minor, $patch = 1..3 | ForEach-Object { [int]$match.Groups[$_].Value }
        $Version = switch ($Bump) {
            'major' { "$($major + 1).0.0" }
            'minor' { "$major.$($minor + 1).0" }
            'patch' { "$major.$minor.$($patch + 1)" }
        }
        $text = $text.Remove($match.Index, $match.Length).Insert($match.Index, "<Version>$Version</Version>")
        [IO.File]::WriteAllText($proj, $text, (New-Object Text.UTF8Encoding($hasBom)))

        git commit --quiet -m "Version $Version" -- $proj
        if ($LASTEXITCODE -ne 0) { throw "git commit failed ($LASTEXITCODE)" }
        # Along with any of your commits that weren't pushed yet.
        git push --quiet origin $default
        if ($LASTEXITCODE -ne 0) { throw "git push failed ($LASTEXITCODE). The version commit is made here: push it, then run .\release.ps1 -Publish." }
        Write-Host "Raised the version to $Version and pushed it."
    }
    finally {
        Pop-Location
    }
    if (-not $Publish) { return }
}

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

    # Only the Start menu icon: neither installer can offer a choice, so the app asks on its first start whether to
    # add one to the desktop (Services\DesktopIcon.cs).
    dotnet vpk pack --packId RepoNLauncher --packVersion $Version --runtime win-x64 --packDir $appDir --mainExe RepoNLauncher.exe `
        --packTitle "Repo'N'Launcher" --packAuthors AhHeng --icon $icon --shortcuts StartMenuRoot --msi --outputDir $outDir
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
