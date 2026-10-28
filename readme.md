# Repo'N'Launcher

A Windows dashboard for the git repos on your PC: see their status, fetch, commit, and start them from one window.

Built with WPF on .NET 8.

## Features

- **Project list in folder sections.** Add a project folder, or scan a folder for every git repo in it. Repos are grouped into collapsible sections by folder: a subfolder with two or more repos gets its own nested section. A section's checkbox ticks all of its repos for **Fetch Selected**.
- **Overview.** Shows the status, with Fetch Latest. You can change the project type (Flutter, .NET, Node, Python) if it was detected wrong. **Open in** has Explorer, Visual Studio, Android Studio and VS Code, plus any apps you add yourself.
  - **Branch:** the branch checked out now. To change it, pick one under **Switch to** and press **Switch**; picking alone does nothing. A branch only the remote has gets a local copy that tracks it. Uncommitted changes come along unless they clash; then the switch stops and says so.
  - **New branch…:** name it (spaces become dashes) and pick where it starts: the current branch, another local branch, or a remote's copy such as `origin/main` as of the last fetch. It's switched to straight away unless you untick that. It stays local until it's pushed, for example by Create Pull Request.
  - **Merge from:** shows the current branch's default: the branch Fetch Latest merges in after pulling (the remote's copy). It's none at first, so Fetch Latest just pulls, except `heng_main`, which merges `development`.
    - Pick a branch and press **Update default** to have Fetch Latest merge it from now on. Defaults are saved per branch.
    - Or press **Merge now** to merge the picked branch just once; the default stays as it is.
  - **Create Pull Request:** from the current branch into a branch you pick on GitHub, with a title, description and draft option filled in from the commits. The branch is pushed first if GitHub doesn't have all of it. Creating it here needs the GitHub sign-in; **Finish on GitHub** opens GitHub's own page instead.
- **Changes.** Changed files with diffs. Commit, discard, stash, and resolve merge conflicts (ours / theirs / mark resolved).
- **Commits.** Recent history.
- **Run.**
  - Starts the project with its live console: `flutter run` on a chosen device, `dotnet run`, or your own start command.
  - **Choose file…** builds the command from a script, e.g. `python app.py`, using the project's `.venv` when it has one.
  - Shows a button for any local URL the app prints.
  - Warns when the app's ports are already in use.
- **Environment.** Lists the values from `.env` and `appsettings*.json`.
- **Phone screen.** Mirrors an Android phone or emulator beside the dashboard through [scrcpy](https://github.com/Genymobile/scrcpy), which the app can download for you.
- **Repos on this PC.** Needs a GitHub sign-in.
  - Finds every git repo on your local drives and asks GitHub who owns each one.
  - Filters them into **All**, **Mine** and **Others**, and adds the ones you pick to the dashboard.
  - Skips Windows, Program Files, AppData, hidden and dot-folders, and dependency folders such as `node_modules`.

- **AI Chat.** Chat with Claude Code or Codex about a project, as in their own apps. It runs the `claude` or `codex` program installed on this PC, which uses its own sign-in.
  - **Composer:** pick the agent, model, effort, permissions and project folder. Type `@` to mention a file. Paste, drop or attach images and other files.
  - **Replies:** they stream in as Markdown. Each tool call is a row you can expand to see its input and output.
  - **Claude's questions:** permission prompts, multiple-choice questions and plan approvals appear as cards you answer in the chat.
  - **Codex:** it doesn't stop to ask. Its sandbox setting (read only, edit project, full access) decides what it may do.
  - **History:** every saved Claude Code and Codex chat on this PC is listed, with search. Open one to read it or carry on.
  - A chat keeps running when the window is closed. Closing the dashboard stops it.
  - Codex is found on PATH, in npm, or inside the Codex app.
- **AI Usage.** Claude Code and Codex usage. The dashboard never reads either tool's sign-in.
  - **Usage bar:** under the header, always in view: each tool's plan and its session and weekly (or monthly) limits. It refreshes every minute while the dashboard is open, except when minimized. **View details** opens the full window.
  - **Sign-in and plan:** whether each tool is installed and signed in, and on which plan.
  - **Plan limits:** the 5-hour, weekly or monthly limits, with when they reset. They're checked with the installed tools when the dashboard starts, every 5 minutes, when the details window opens, and on Refresh: Claude Code's `/usage` command (run locally, no model call, no saved chat) and Codex's app server. Each tool fetches them with its own sign-in. Without the tool, the last saved figures show.
  - **Token use:** today, the last 7 and 30 days, and per model, from the session files.
  - Codex use is counted from the sessions saved on this PC, which come from Codex CLI, the IDE extension and AI Chat.

## GitHub sign-in

GitHub sign-in is optional; **Repos on this PC** and creating a pull request from the dashboard use it. You can sign in in two ways:

- **Sign in with browser** uses the GitHub sign-in Git already has (Git Credential Manager). If Git isn't signed in yet, GitHub opens in your browser.
- **Token:** paste a personal access token with the `repo` scope.

The token is encrypted for your Windows account (DPAPI) and kept apart from the settings file. **Sign out** deletes it.

## Build and run

Requires the .NET 8 SDK on Windows.

```powershell
dotnet run --project src\RepoNLauncher
```

To build the single-file exe (`publish\RepoNLauncher.exe`), add a desktop shortcut and start it:

```powershell
.\publish.ps1                         # closes a running copy first
.\publish.ps1 -NoShortcut -NoLaunch
```

## Where settings live

- `%APPDATA%\RepoNLauncher\config.json` holds your projects, scan folders, section states and Open-in apps.
- `%APPDATA%\RepoNLauncher\github-sign-in.dat` holds the encrypted GitHub sign-in.
- Debug builds use `%APPDATA%\RepoNLauncher-Dev` instead, so running from source never touches the installed app's settings.
