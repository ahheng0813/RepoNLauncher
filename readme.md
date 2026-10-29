# Repo'N'Launcher

A Windows dashboard for the git repos on your PC: see their status, fetch, commit, and start them from one window.

## Download

Get the latest version from [Releases](https://github.com/ahheng0813/RepoNLauncher/releases/latest). It runs on 64-bit Windows 10 and 11, with nothing else to install for the app itself.

<div class=dwld_table>
  <table align="center">
    <thead>
      <tr>
        <th width="46%", align="center">File type</th>
        <th width="500px", align="center">Download</th>
      </tr>
    </thead>
    <tbody>
      <tr>
        <td align="left"><code>.exe</code> installer (recommended)</td>
        <td align="center"><a href="https://github.com/ahheng0813/RepoNLauncher/releases/latest/download/RepoNLauncher-win-Setup.exe">V1.1.0</a></td>
      </tr>
      <tr>
        <td align="left"><code>.msi</code> install wizard</td>
        <td align="center"><a href="https://github.com/ahheng0813/RepoNLauncher/releases/latest/download/RepoNLauncher-win.msi">V1.1.0</a></td>
      </tr>
      <tr>
        <td align="left"><code>.zip</code> portable</td>
        <td align="center"><a href="https://github.com/ahheng0813/RepoNLauncher/releases/latest/download/RepoNLauncher-win-Portable.zip">V1.1.0</a></td>
      </tr>
    </tbody>
  </table>
</div>

The installers add a Start menu icon. The first time the app starts, it asks whether to add one to your desktop too.

The files aren't code-signed yet, so Windows may warn before the first run: choose **More info**, then **Run anyway**.

**Updates:** every copy updates itself. The app checks for a new version when it starts and every 4 hours, downloads it in the background, and then shows **Restart to update** in the status bar. If you don't restart, the update is applied the next time the app starts. The status bar also shows your version and has **Check for updates**.

### What else you need

- [Git for Windows](https://git-scm.com/download/win).
- To run a project from the dashboard, the tools it runs with: Flutter, the .NET SDK, Node.js or Python.
- For **AI Chat** and the AI usage bar, Claude Code or Codex installed and signed in.
- For **Phone screen**, an Android phone with USB debugging on, or an emulator. The app downloads scrcpy for you.
- For **Android emulators**, Android Studio's SDK with the Android Emulator and at least one system image (SDK Manager in Android Studio).

## Features

- **Projects in folder sections.** Add a project folder, or scan a folder for every git repo in it. Repos are grouped by folder into collapsible sections. Tick repos, or a whole section, for **Fetch Selected**.
- **Overview.** The repo's status, **Fetch Latest**, and its project type. **Open in** has Explorer, Visual Studio, Android Studio, VS Code, the repo's page on **GitHub** (when it has a GitHub remote) and any apps you add.
  - **Branch:** the branch checked out now. Pick another under **Switch to** and press **Switch**, or make one with **New branch…**.
  - **Publish branch:** shown while the current branch is only on this PC, such as one just made with **New branch…**. It pushes the branch to the remote, and **Fetch Latest** pulls it from there afterwards.
  - **Push:** shown while the current branch has commits that its remote branch doesn't, such as ones you just committed or merged in. Until you push, they're only on this PC, even though **Commits** lists them.
  - **Merge from:** the branch **Fetch Latest** merges in after pulling. Pick one and press **Update default** to keep it, or **Merge now** to merge it just once.
  - **Create Pull Request:** from the current branch, or another picked under **From** (one on this PC or one only GitHub has), with the title and description filled in from its commits. A branch GitHub doesn't have yet, or is missing commits of, is pushed first.
- **Changes.** Changed files with diffs. Commit, discard, stash, and resolve merge conflicts.
- **Commits.** Recent history.
- **Run.** Starts the project with a live console: `flutter run` on a device, `dotnet run`, or your own command. **Choose file…** builds the command from a script.
  - **Save** keeps the typed command in a list under it, so a project can have several: each saved command has its own ▶ to run it with one click, and ✕ to remove it.
  - Type in the box under the console to answer the app's prompts. Flutter projects also get **Hot reload** and **Hot restart**.
  - A button opens any local URL the app prints, and you're warned when its ports are already in use.
- **Environment.** The values from `.env` and `appsettings*.json`.
- **Phone screen.** Mirrors an Android phone or emulator beside the dashboard through [scrcpy](https://github.com/Genymobile/scrcpy). The buttons under the device list work on the device picked there, docked or popped out:
  - **Screenshot:** saves the screen at full resolution to `Pictures\Repo'N'Launcher` and copies it, ready to paste.
  - **Record screen:** records what's shown to `Videos\Repo'N'Launcher`, with the phone's sound while it plays on this PC (Android 11 or later). Press it again to save; mirroring carries on. Closing the dashboard saves a recording in progress too.
  - **Record voice:** records the phone's microphone to `Music\Repo'N'Launcher` (Android 11 or later), alongside the mirror. An Android Studio emulator's microphone is this PC's once **Virtual microphone uses host audio input** is on in its Extended controls.
  - **Camera:** shows the phone's back or front camera instead of its screen (Android 12 or later). Screenshot then takes a photo, as big as the camera is shown (pop out or go full screen for a bigger one), and Record records video with the microphone's sound.
  - **Location:** gives the device a virtual location. Pick the place on the map: search for a place or address, click the map to put the pin there (or drag the pin), type or paste latitude, longitude (from Google Maps, say), or use this PC's location from Windows. The map and the coordinates follow each other; then press **Set location**. An Android Studio emulator's GPS moves there. A phone (Android 12 or later) reports it to its apps until you give it its real location back, or close the dashboard. The map and place search are [OpenStreetMap](https://www.openstreetmap.org/copyright)'s, so they need the internet.
  - **Files:** the phone's storage in a window of its own, with its SD card if it has one, and shortcuts to Download, Camera, Pictures, Documents, Movies and Music. Double-click a folder to go in, or a file to open a copy of it on this PC.
    - **Send files here…**, **Send a folder…**, or dropping files on the list, copies them from this PC into the folder shown. **Save to PC…** copies what's selected to a folder on this PC.
    - **New folder**, **Rename** (F2) and **Delete** change the phone's files. Deleting can't be undone: the phone has no recycle bin for it.
    - It asks before replacing anything with the same name, and **Stop** cancels a copy and removes what it left half copied.
    - Files dropped on the Phone screen's buttons go straight to the phone's Download folder.
  - After each screenshot or recording, the file is shown under the buttons to open or find in its folder.
- **Android emulators.** The phone button in the Phone screen's header, or **Emulators…** in a Flutter project's Run tab, lists the emulators on this PC to start. You can also create one: pick a name, a size (small phone, medium phone or tablet) and an installed Android version. A started emulator joins the Phone screen and Flutter device lists.
- **Repos on this PC.** Finds every git repo on your drives and sorts them into **Mine** and **Others** by GitHub owner, so you can add them to the dashboard.
- **Clone from GitHub.** Lists every repo your GitHub account can see (yours, your organizations' and ones you collaborate on) and marks each **On this PC** or **Not on this PC**, checking the dashboard, the clone folder and your drives. Search by name or description.
  - Tick as many as you like, pick a branch for any that shouldn't use the default, and press **Clone**. They're cloned one after another into the **Clone into** folder, each in a folder named after the repo, and added to the dashboard.
  - **Stop** cancels the clone in progress and removes its half-made folder.
  - A repo already on this PC shows where it is, with **Add to dashboard** if it isn't there yet.
- **AI Chat.** Chat with Claude Code or Codex about a project, using the one installed on this PC.
  - Type `@` to mention a file, and paste or drop images and files.
  - Answer Claude's permission prompts, questions and plans in the chat.
  - In a long chat, the prompt you're reading stays pinned at the top. **Prompts** lists them all, and **Jump to latest** takes you back down.
  - **History** lists every saved Claude Code and Codex chat on this PC, with search.
  - A chat keeps running when its window is closed. Closing the dashboard stops it.
- **AI usage bar.** Your Claude Code and Codex plan limits (5-hour, weekly or monthly) under the header, always in view. **View details** adds token use by day and by model. The dashboard never reads either tool's sign-in.

## GitHub sign-in

Optional: only **Repos on this PC**, **Clone from GitHub** and **Create Pull Request** use it.

- **Sign in with browser** uses the GitHub sign-in Git already has, or opens GitHub in your browser.
- **Token:** paste a personal access token with the `repo` scope.

The sign-in is encrypted for your Windows account, and **Sign out** deletes it. Cloning, like fetching and pushing, uses Git's own GitHub sign-in, so for a private repo Git asks for one if it doesn't have it yet.

## Where settings live

In `%APPDATA%\RepoNLauncher`: `config.json` holds your projects and settings, and `github-sign-in.dat` the encrypted GitHub sign-in. The Location map keeps the map pictures it has shown in `%LOCALAPPDATA%\RepoNLauncher\MapTiles` (up to about 200 MB), so it doesn't download them again.

## Building from source

See [DEVELOPMENT.md](DEVELOPMENT.md).
