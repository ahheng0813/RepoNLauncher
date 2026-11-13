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
        <td align="center"><a href="https://github.com/ahheng0813/RepoNLauncher/releases/latest/download/RepoNLauncher-win-Setup.exe">V2.3.3</a></td>
      </tr>
      <tr>
        <td align="left"><code>.msi</code> install wizard</td>
        <td align="center"><a href="https://github.com/ahheng0813/RepoNLauncher/releases/latest/download/RepoNLauncher-win.msi">V2.3.3</a></td>
      </tr>
      <tr>
        <td align="left"><code>.zip</code> portable</td>
        <td align="center"><a href="https://github.com/ahheng0813/RepoNLauncher/releases/latest/download/RepoNLauncher-win-Portable.zip">V2.3.3</a></td>
      </tr>
    </tbody>
  </table>
</div>

The installers add a Start menu icon. The first time the app starts, it asks whether to add one to your desktop too.

The files aren't code-signed yet, so Windows may warn before the first run: choose **More info**, then **Run anyway**.

**Updates:** every copy updates itself. The app checks for a new version when it starts and every 4 hours, downloads it in the background, and then shows **Restart to update** in the status bar. If you don't restart, the update is applied the next time the app starts. The status bar also shows your version and has **Check for updates**, as does **Settings > Updates**. If an update can't be installed, for example because another program is using the app's folder, the app says so and offers **Restart to update** again.

**Uninstall:** **Uninstall…** in the status bar, or in **Settings > General**, removes the app and everything it keeps on this PC: your project list and settings, the GitHub sign-in, the scrcpy it downloaded, its caches, temp files and update files, and its desktop icon. Your projects' own folders aren't touched. Uninstalling from Windows' **Settings > Apps** removes only the app.

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
  - **Flutter devices:** **Run** starts the app on the device picked beside **Refresh Devices**. To run it on another device too, pick that one and press **Run** again. Each device gets a tab over the console with its own output, and a green dot while the app runs there. **Stop**, **Restart**, **Hot reload**, **Hot restart** and what you type go to the tab that's open, and picking a tab picks its device. ✕ on a tab stops the app on that device and closes the tab. A command of your own that names its device with `-d` runs in that device's tab.
  - **Restart** stops the app whose console shows and runs it again, with the same command on the same device, once it has ended, so its port is free. On a run that has stopped, it just runs it again.
  - **Save** keeps the typed command in a list under it, so a project can have several: each saved command has its own ▶ to run it with one click, and ✕ to remove it.
  - Type in the box under the console to answer the app's prompts. Flutter projects also get **Hot reload** and **Hot restart**.
  - Drag over the output to select it, and **Ctrl+C** to copy (or right-click: **Copy**, **Copy all**, **Select all**). **Ctrl+click** an address the app printed to open it.
  - **Search the output** (**Ctrl+F**) marks every match; **Enter** / **Shift+Enter** (or **F3** / **Shift+F3**) step through them.
  - The ⧉ button opens the console in a window of its own, to keep on another screen. Each project has its own window, so several runs can be watched side by side; each has **Run** / **Stop** / **Restart**, moves to the next screen, and can be pinned on top.
  - You're warned when the app's ports are already in use.
  - **Site:** when the app prints the local address it serves on (a dev server such as Vite or Next, `dotnet run`, Django, Flask, or Flutter's web server), the site opens by itself, and the address becomes a button that opens it again. Untick **Open it when the app starts serving it** to open it only from the button.
    - **Open in** picks where: the dashboard browser, a browser window of the dashboard's own, on another screen than the dashboard's (the default) or on a screen you pick; or your default browser, the one Windows opens links in, shown by name, such as **Default browser (Google Chrome)**. It's the same for every project, and in **Settings > Browser**. Picking another screen moves the open windows there.
    - Right-click a site's address to open it the other way just once: **Open in the dashboard browser**, or **Open in** your default browser.
    - The browser window fills a screen of its own; on the dashboard's screen it leaves the dashboard showing around it. Each project's site has a tab of its own, so running it again shows the new run's site in that tab, whichever window it's been moved to. If the server takes a moment to answer, the tab waits for it.
    - It has back, forward, reload, the address (Ctrl+L), **New window**, **Open in your default browser**, developer tools (F12), **Move to the next screen**, **Pin on top**, and **Full screen** (F11, Esc to leave). On a window pinned on top, its developer tools and the pop-ups its pages open show over it. A local site's own HTTPS certificate, such as ASP.NET's development one, is accepted.
    - **Tabs:** **+** above the address (Ctrl+T, or double-click past the tabs) opens a tab; × or the middle button closes one (Ctrl+W), and Ctrl+Shift+T opens the last one closed again. Ctrl+Tab and Ctrl+Shift+Tab go to the next and previous tab, Ctrl+1 to Ctrl+8 to that tab, and Ctrl+9 to the last. A link that opens a new window opens in a tab instead (with Ctrl held, behind the one shown; with Shift, in a window of its own), except a pop-up the page sizes itself, such as a sign-in's. Right-click a tab for **New tab to the right**, **Reload**, **Duplicate**, **Move to new window**, and closing it, the others, or those to its right.
    - **Dragging tabs**, as in Chrome: drag one along the tabs to move it; drag it off them to give it a window of its own, which follows the mouse until you let go; drop it onto another browser window's tabs to move it there. Dragging a window's only tab drags the window. The page keeps going wherever it's moved. Incognito and ordinary tabs don't mix.
    - **More windows:** **New window** (Ctrl+N) beside the address opens another, Ctrl+Shift+N an incognito one.
    - **No run needed:** **Browser** in the dashboard's header brings up the browser window you used last that has no project's site in it, or opens one, and **Open dashboard browser** beside **Open in** opens the project's own tab, before it has run. Opened like this, a tab starts on a page that asks for an address (such as `localhost:3000`) and lists your saved sites.
    - **Saved sites:** ☆ beside the address (**Ctrl+D**) saves the page shown, named after its title, and opens a box to rename it, change its address or **Remove** it; on a saved site the star is lit, and pressing it opens that box again. Saved sites show in a bar under the address in every browser window, one click each; right-click one to open it in your default browser, edit or remove it.
    - **Browsing data:** by default the dashboard browser remembers history, cookies and sign-ins, form entries, site storage and the cache, so sites stay signed in. In **Settings > Browser**, pick **Incognito** to keep none of it on this PC: it's all forgotten once every browser window is closed, and those windows show "(Incognito)" in their title. It applies to browser windows opened from then on. **Clear browsing data** there clears everything kept so far.
    - **Ad blocker:** on by default. It blocks ad and tracker servers, and hides the spaces pages leave for ads, using the EasyList, EasyPrivacy, Peter Lowe and Fanboy's Annoyance filter lists. It downloads them when a browser window first opens, and again once they're 4 days old. Pick how strong it is in **Settings > Browser**, or with the shield beside the address:
      - **Light:** ad servers only. Pages keep their layout, with gaps where ads were.
      - **Balanced** (the default): ads and trackers, with the spaces left for ads hidden.
      - **Strict:** also cookie banners and social widgets, more ad and tracker servers, and pop-ups a page opens without a click. Some sites may break.
      - The shield shows how many requests it blocked on the page. Press it to turn the blocker on or off, change its strength, or **Pause** it on the page's site until the app closes; the page reloads to match. Sites paused this way are listed in Settings, with **Resume**.
      - Sites on this PC or its network, such as `localhost:3000`, aren't filtered, as they're your own projects.
- **Environment.** The values from `.env` and `appsettings*.json`.
- **Phone screen.** Mirrors Android phones and emulators beside the dashboard through [scrcpy](https://github.com/Genymobile/scrcpy). Click the mirror to type on the device: letters, digits and symbols all go to it as key presses. This PC's clipboard is left alone: in the mirror, **Alt+V** pastes what's copied on this PC, and **Alt+C** copies what's selected on the device to this PC (an emulator also shares the clipboard itself, so Ctrl+V pastes there too). A device that's still starting up, such as an emulator just started, shows once Android has finished starting. The device list also has the emulators on this PC that aren't running: pick one and press **Show**, and it starts (in its own window, as from Android Studio) and its screen shows here once it's up. Several can show at once: pick another device and press **Show**, and it shows beside the others with its name above it, the panel widening to fit them. Clicking a device, or its name, picks it; **Stop** stops just that one. **Pop out** puts the device picked in a window of its own, with **Pin on top**, **Full screen** and its own buttons: each device can have one, and they open side by side. The buttons under the device list work on the device picked there:
  - **Screenshot:** saves the screen at full resolution to `Pictures\Repo'N'Launcher` and copies it, ready to paste.
  - **Record screen:** records what's shown to `Videos\Repo'N'Launcher`, with the phone's sound while it plays on this PC (Android 11 or later). Press it again to save; mirroring carries on. Closing the dashboard saves a recording in progress too.
  - **Record voice:** records the phone's microphone to `Music\Repo'N'Launcher` (Android 11 or later), alongside the mirror. An Android Studio emulator's microphone is this PC's once **Virtual microphone uses host audio input** is on in its Extended controls.
  - **Camera:** shows the phone's back or front camera instead of its screen (Android 12 or later). Screenshot then takes a photo, as big as the camera is shown (pop out or go full screen for a bigger one), and Record records video with the microphone's sound.
  - **Location:** gives the device a virtual location. Pick the place on the map: search for a place or address, click the map to put the pin there (or drag the pin), type or paste latitude, longitude (from Google Maps, say), or use this PC's location from Windows. The map and the coordinates follow each other; then press **Set location**. An Android Studio emulator's GPS moves there. A phone (Android 12 or later) reports it to its apps until you give it its real location back, or close the dashboard. Some makers' phones (Xiaomi's, OPPO's and others) need a Developer options setting turned on first; the dashboard says which. The map and place search are [OpenStreetMap](https://www.openstreetmap.org/copyright)'s, so they need the internet.
  - **Files:** the phone's storage in a window of its own, with its SD card if it has one, and shortcuts to Download, Camera, Pictures, Documents, Movies and Music. Double-click a folder to go in, or a file to open a copy of it on this PC.
    - **Send files here…** and **Send a folder…** copy files from this PC into the folder shown. **Save to PC…** copies what's selected to a folder on this PC.
    - Drop files on the list, from File Explorer or another device's files, to copy them into the folder shown, or onto a folder in the list to copy them into that one. Drag files out of the list onto a File Explorer window or the desktop to copy them to that folder, or onto another device's files.
    - **Copy** (Ctrl+C) and **Paste** (Ctrl+V) do the same: copy files here and paste them in another folder, in another device's files, or in File Explorer, or copy them in File Explorer and paste them here. Pasted in the folder they're in, they're copies named "… - Copy". A picture you've copied, such as a screenshot, pastes as a PNG. File Explorer can only paste files that are on this PC, so copying starts copying them to this PC straight away; pasting on a device doesn't wait for that.
    - Before anything is dropped or pasted, it shows what's coming, with the photos and videos, and asks whether to paste it at the device (or This PC) and folder it's going to.
    - **New folder**, **Rename** (F2) and **Delete** change the phone's files. Deleting can't be undone: the phone has no recycle bin for it.
    - It asks before replacing anything with the same name, and **Stop** cancels a copy and removes what it left half copied.
    - Files dropped on the Phone screen's buttons go straight to the phone's Download folder.
  - **Console:** the Run console of the app the dashboard runs on the device, in a window of its own with that device's tab open, so its output, **Hot reload** and **Stop** are beside the phone.
  - After each screenshot or recording, the file is shown under the buttons to open or find in its folder.
- **Android emulators.** The phone button in the Phone screen's header, **Emulators…** in a Flutter project's Run tab, or **Settings > Emulators** lists the emulators on this PC. A started emulator joins the Phone screen and Flutter device lists under its own name, such as Medium Phone API 36.1, rather than the "sdk gphone64 x86 64" Android gives every emulator.
  - **Start** opens one in its own window; **Shut down** closes a running one.
  - **Rename** (the pencil) changes the name it's shown by, in Android Studio too. Its AVD name stays, so `flutter emulators --launch` and scripts still find it. Two emulators can't share a name.
  - **Delete** (the bin) removes it with its apps, files and settings, after asking. A running one has to be shut down first.
  - **Create an emulator:** pick a name, a size (small phone, medium phone or tablet) and an installed Android version.
- **Repos on this PC.** Finds every git repo on your drives and sorts them into **Mine** and **Others** by GitHub owner, so you can add them to the dashboard.
- **Clone from GitHub.** Lists every repo your GitHub account can see (yours, your organizations' and ones you collaborate on) and marks each **On this PC** or **Not on this PC**, checking the dashboard, the clone folder and your drives. Search by name or description.
  - Tick as many as you like, pick a branch for any that shouldn't use the default, and press **Clone**. They're cloned one after another into the **Clone into** folder, each in a folder named after the repo, and added to the dashboard.
  - **Stop** cancels the clone in progress and removes its half-made folder.
  - A repo already on this PC shows where it is, with **Add to dashboard** if it isn't there yet.
- **AI Chat.** Chat with Claude Code or Codex about a project, using the one installed on this PC.
  - Type `@` to mention a file, and paste or drop images and files.
  - **↑** in the message box brings back what you sent before in that chat, to send again or change; press it again to go further back. **↓** goes forward again, and past the newest, back to what you were typing.
  - Pick the model, effort (low to max, including **Extra high**), permissions and folder under the message box. **Default** leaves them to Claude Code's own settings and says what those pick, such as **Default (Opus)** and **Default effort (Extra high)**.
  - Answer Claude's permission prompts, questions and plans in the chat.
  - In a long chat, the prompt you're reading stays pinned at the top. **Prompts** lists them all, and **Jump to latest** takes you back down.
  - **History** lists every saved Claude Code and Codex chat on this PC, with search.
  - A chat keeps running when its window is closed. Closing the dashboard stops it.
- **Settings.** The gear at the right of the header, or **Ctrl+,**. Clicking the version in the status bar opens its **About** page.
  - **Date & time:** how dates and times are shown: Windows' own format, a ready-made one (2026-03-25, 25/03/2026, 25 Mar 2026, 24-hour or 12-hour, with or without seconds), or a pattern of your own, with a preview. It's used for commit dates in **Commits**, file dates in the phone's **Files**, and times such as when updates and AI usage were last checked. Dates are in this PC's time zone.
  - **General:** the GitHub account, where a project's local site opens (the dashboard browser or your default browser, as in the Run tab), the open-in apps, the desktop icon (installed app), the folder your settings are kept in, and **Uninstall…**.
  - **Emulators:** the emulators on this PC, to start, shut down, rename, delete or create, as in the Android emulators window.
  - **Updates:** your version, **Check for updates**, **Restart to update** once one is ready, and the release notes on GitHub.
  - **About:** the author, the dashboard's repo on GitHub, where to report a problem, the license, and **Copy version info** for a bug report.
- **Windows of their own.** AI Chat, AI usage, Repos on this PC, Clone from GitHub, Android emulators, the phone's Files, Settings, Run consoles and site windows each have a pin in their header to keep them on top of other windows. Opening one that's open already brings it to the front, restored if it was minimized.
- **AI usage bar.** Your Claude Code and Codex plan limits (5-hour, weekly or monthly) under the header, always in view. **View details** adds token use by day and by model. The dashboard never reads either tool's sign-in.

## GitHub sign-in

Optional: only **Repos on this PC**, **Clone from GitHub** and **Create Pull Request** use it.

- **Sign in with browser** uses the GitHub sign-in Git already has, or opens GitHub in your browser.
- **Token:** paste a personal access token with the `repo` scope.

The sign-in is encrypted for your Windows account, and **Sign out** deletes it. Cloning, like fetching and pushing, uses Git's own GitHub sign-in, so for a private repo Git asks for one if it doesn't have it yet.

## Where settings live

In `%APPDATA%\RepoNLauncher` (**Settings > General > Open folder**): `config.json` holds your projects and settings, and `github-sign-in.dat` the encrypted GitHub sign-in. The Location map keeps the map pictures it has shown in `%LOCALAPPDATA%\RepoNLauncher\MapTiles` (up to about 200 MB), so it doesn't download them again. The dashboard browser keeps its data in `%LOCALAPPDATA%\RepoNLauncher\Browser`, and the ad blocker's filter lists in `%LOCALAPPDATA%\RepoNLauncher\AdBlock`.

## Building from source

See [DEVELOPMENT.md](DEVELOPMENT.md).
