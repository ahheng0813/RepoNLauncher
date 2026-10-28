# Developing Repo'N'Launcher

WPF on .NET 8. Requires the .NET 8 SDK on Windows.

## Build and run

```powershell
dotnet run --project src\RepoNLauncher
```

Debug builds keep their settings in `%APPDATA%\RepoNLauncher-Dev` instead of `%APPDATA%\RepoNLauncher`, so running from source never touches the installed app's settings.

To build the single-file exe (`publish\RepoNLauncher.exe`), add a desktop shortcut and start it:

```powershell
.\publish.ps1                         # closes a running copy first
.\publish.ps1 -NoShortcut -NoLaunch
```

That exe doesn't update itself; the installed app does.

## Installers and updates

[Velopack](https://velopack.io) builds the installers into `release\out`:

- `RepoNLauncher-win-Setup.exe`: installs for the current user, no admin needed.
- `RepoNLauncher-win.msi`: install wizard, for the current user or everyone on the PC.
- `RepoNLauncher-win-Portable.zip`: runs without installing.
- The packages and `releases.win.json` that installed copies update from.

The app checks the repo's GitHub releases when it starts and every 4 hours (`Services\UpdateService.cs`). Checking needs the releases to be public: while the repo is private, the status bar says it couldn't check.

`vpk` is pinned in `.config\dotnet-tools.json`, and `release.ps1` restores it.

## Making a release

1. Raise `<Version>` in `src\RepoNLauncher\RepoNLauncher.csproj`. Installed copies only update to a higher version.
2. Build the installers into `release\out`:

   ```powershell
   .\release.ps1
   ```

3. Publish them as a GitHub release tagged `v<version>`, which installed copies then update to. This needs the GitHub CLI signed in (`gh auth login`):

   ```powershell
   .\release.ps1 -Publish
   ```

To try an update before publishing, set `REPONLAUNCHER_UPDATE_FEED` to a folder of releases (such as `release\out`) before starting the installed app. It then updates from there instead of GitHub.

The installers aren't signed, so Windows SmartScreen warns before the first install. `vpk pack` can sign them with `--signParams` or `--azureTrustedSignFile`.
