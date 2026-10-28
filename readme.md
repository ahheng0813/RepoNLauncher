# Repo'N'Launcher

A Windows dashboard for the git repos on your PC: see their status, fetch, commit, and start them from one window.

## Download

Get the latest version from [Releases](https://github.com/ahheng0813/RepoNLauncher/releases/latest). It runs on 64-bit Windows 10 and 11, with nothing else to install for the app itself.

| File type | Download |
| --- | --- |
| `.exe` installer (recommended) | [![Download the latest version](https://img.shields.io/github/v/release/ahheng0813/RepoNLauncher?label=Download&color=2ea44f)](https://github.com/ahheng0813/RepoNLauncher/releases/latest/download/RepoNLauncher-win-Setup.exe) |
| `.msi` install wizard | [![Download the latest version](https://img.shields.io/github/v/release/ahheng0813/RepoNLauncher?label=Download&color=2ea44f)](https://github.com/ahheng0813/RepoNLauncher/releases/latest/download/RepoNLauncher-win.msi) |
| `.zip` portable | [![Download the latest version](https://img.shields.io/github/v/release/ahheng0813/RepoNLauncher?label=Download&color=2ea44f)](https://github.com/ahheng0813/RepoNLauncher/releases/latest/download/RepoNLauncher-win-Portable.zip) |

The files aren't code-signed yet, so Windows may warn before the first run: choose **More info**, then **Run anyway**.

**Updates:** every copy updates itself. The app checks for a new version when it starts and every 4 hours, downloads it in the background, and then shows **Restart to update** in the status bar. If you don't restart, the update is applied the next time the app starts. The status bar also shows your version and has **Check for updates**.

### What else you need

- [Git for Windows](https://git-scm.com/download/win).
- To run a project from the dashboard, the tools it runs with: Flutter, the .NET SDK, Node.js or Python.
- For **AI Chat** and the AI usage bar, Claude Code or Codex installed and signed in.
- For **Phone screen**, an Android phone with USB debugging on, or an emulator. The app downloads scrcpy for you.

## Features

- **Projects in folder sections.** Add a project folder, or scan a folder for every git repo in it. Repos are grouped by folder into collapsible sections. Tick repos, or a whole section, for **Fetch Selected**.
- **Overview.** The repo's status, **Fetch Latest**, and its project type. **Open in** has Explorer, Visual Studio, Android Studio, VS Code and any apps you add.
  - **Branch:** the branch checked out now. Pick another under **Switch to** and press **Switch**, or make one with **New branch…**.
  - **Merge from:** the branch **Fetch Latest** merges in after pulling. Pick one and press **Update default** to keep it, or **Merge now** to merge it just once.
  - **Create Pull Request:** from the current branch, with the title and description filled in from its commits.
- **Changes.** Changed files with diffs. Commit, discard, stash, and resolve merge conflicts.
- **Commits.** Recent history.
- **Run.** Starts the project with a live console: `flutter run` on a device, `dotnet run`, or your own command. **Choose file…** builds the command from a script.
  - Type in the box under the console to answer the app's prompts. Flutter projects also get **Hot reload** and **Hot restart**.
  - A button opens any local URL the app prints, and you're warned when its ports are already in use.
- **Environment.** The values from `.env` and `appsettings*.json`.
- **Phone screen.** Mirrors an Android phone or emulator beside the dashboard through [scrcpy](https://github.com/Genymobile/scrcpy).
- **Repos on this PC.** Finds every git repo on your drives and sorts them into **Mine** and **Others** by GitHub owner, so you can add them to the dashboard.
- **AI Chat.** Chat with Claude Code or Codex about a project, using the one installed on this PC.
  - Type `@` to mention a file, and paste or drop images and files.
  - Answer Claude's permission prompts, questions and plans in the chat.
  - In a long chat, the prompt you're reading stays pinned at the top. **Prompts** lists them all, and **Jump to latest** takes you back down.
  - **History** lists every saved Claude Code and Codex chat on this PC, with search.
  - A chat keeps running when its window is closed. Closing the dashboard stops it.
- **AI usage bar.** Your Claude Code and Codex plan limits (5-hour, weekly or monthly) under the header, always in view. **View details** adds token use by day and by model. The dashboard never reads either tool's sign-in.

## GitHub sign-in

Optional: only **Repos on this PC** and **Create Pull Request** use it.

- **Sign in with browser** uses the GitHub sign-in Git already has, or opens GitHub in your browser.
- **Token:** paste a personal access token with the `repo` scope.

The sign-in is encrypted for your Windows account, and **Sign out** deletes it.

## Where settings live

In `%APPDATA%\RepoNLauncher`: `config.json` holds your projects and settings, and `github-sign-in.dat` the encrypted GitHub sign-in.

## Building from source

See [DEVELOPMENT.md](DEVELOPMENT.md).
