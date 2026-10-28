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

Both installers add only a Start menu icon, since neither can offer a choice. The installed app asks on its first start whether to add a desktop icon (`Services\DesktopIcon.cs`), once per Windows user. Velopack updates that icon with its own and removes it on uninstall.

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

4. The **Readme version** workflow (`.github\workflows\readme-version.yml`) then puts the new version on the readme's download links and commits that to `main`. Pull before your next push. It can also be run by hand from the repo's Actions tab.

### Raising the version with the script

`-Bump` raises the version, commits just that as "Version x.y.z" and pushes it. With `-Publish` it then releases it, so this one command does steps 1 to 3:

```powershell
.\release.ps1 -Bump ask -Publish
```

| `-Bump` | New version, from 1.0.0 |
| --- | --- |
| `patch` | 1.0.1 |
| `minor` | 1.1.0 |
| `major` | 2.0.0 |
| `1.2.0` | the version given |
| `ask` | asks "New version [1.0.1]:" and takes what you type, or the suggestion on Enter |

- The new version must be higher than the current one, since installed copies only update to a higher version. `ask` asks again until it is.
- It stops before changing anything if there are uncommitted changes, if you're not on `main`, or if GitHub has commits you haven't pulled, so the release always matches what's on GitHub.
- It works as a saved start command in the dashboard's Run tab: `powershell -NoProfile -File release.ps1 -Bump ask -Publish`. Answer the question in the box under the console.
- If the release fails after the version was pushed, run `.\release.ps1 -Publish` to try again.

To try an update before publishing, set `REPONLAUNCHER_UPDATE_FEED` to a folder of releases (such as `release\out`) before starting the installed app. It then updates from there instead of GitHub.

The installers aren't signed, so Windows SmartScreen warns before the first install. `vpk pack` can sign them with `--signParams` or `--azureTrustedSignFile`.
