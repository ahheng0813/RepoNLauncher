using System.Collections.ObjectModel;
using System.Windows.Forms;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoNLauncher.Models;
using RepoNLauncher.Services;

namespace RepoNLauncher.ViewModels;

/// <summary>
/// A choice in the Run tab's Open in picker, and Settings': the dashboard's browser window on a screen (a null
/// <see cref="Screen"/> picks another than the dashboard's when it opens), or the PC's default browser.
/// </summary>
public sealed class SiteTarget(string label, string? screen, bool isDefaultBrowser)
{
    public string Label { get; } = label;

    /// <summary>The screen's device name, such as \\.\DISPLAY2.</summary>
    public string? Screen { get; } = screen;
    public bool IsDefaultBrowser { get; } = isDefaultBrowser;
    public override string ToString() => Label;
}

/// <summary>
/// A site to show in a project's browser window, on <see cref="Screen"/>; <see cref="Activate"/> brings it to the front.
/// A null <see cref="ProjectPath"/> is the browser of no project's, opened from the dashboard's header; a null
/// <see cref="Url"/> just opens the window, on its start page when it wasn't open.
/// </summary>
public sealed record SiteRequest(string? ProjectPath, string? ProjectName, string? Url, string? Screen, bool Activate);

/// <summary>
/// Where the Run tab opens the site a project serves: the dashboard's own browser window, on a screen of its own
/// (another than the dashboard's by default), or the PC's default browser, the one Windows opens links in. One choice
/// for every project, in the Run tab and Settings; a site's address can be opened the other way just once.
/// </summary>
public partial class SiteBrowserViewModel : ObservableObject
{
    private readonly Action<string?, bool> _save;
    private readonly Action<IReadOnlyList<SavedSite>> _saveSites;

    // What's saved, kept while the screen is unplugged so picking it comes back when it is again.
    private string? _screen;
    private bool _inDefaultBrowser;

    // The screens the picker lists, so opening it only lists them again when they changed.
    private string _listedScreens = string.Empty;
    private bool _listing;

    public ObservableCollection<SiteTarget> Targets { get; } = new();

    [ObservableProperty] private SiteTarget? _selectedTarget;

    /// <summary>The sites saved in the dashboard browser, shared by every browser window.</summary>
    public ObservableCollection<SavedSite> SavedSites { get; } = new();

    /// <summary>Raised to show a site in the browser window; the dashboard opens one, or uses the project's open one.</summary>
    public event Action<SiteRequest>? WindowRequested;

    /// <summary>Raised when another screen is picked, so the browser windows open now move to it.</summary>
    public event Action<string?>? ScreenPicked;

    public SiteBrowserViewModel(string? screen, bool inDefaultBrowser, IEnumerable<SavedSite> savedSites,
        Action<string?, bool> save, Action<IReadOnlyList<SavedSite>> saveSites)
    {
        _screen = screen;
        _inDefaultBrowser = inDefaultBrowser;
        _save = save;
        _saveSites = saveSites;
        foreach (var site in savedSites.Where(s => !string.IsNullOrWhiteSpace(s.Url))) SavedSites.Add(site);
        ListTargets();
    }

    /// <summary>
    /// Lists the screens again, such as when the picker opens, so one plugged in a moment ago is there, and the default
    /// browser by its name, in case another has been made the default since.
    /// </summary>
    public void ListTargets()
    {
        var screens = Screen.AllScreens.OrderBy(s => s.Bounds.Left).ThenBy(s => s.Bounds.Top).ToList();
        var defaultBrowser = DefaultBrowser.Label;
        var listed = string.Join("|", screens.Select(s => $"{s.DeviceName} {s.Bounds}")) + "|" + defaultBrowser;
        if (listed == _listedScreens) return;
        _listedScreens = listed;

        _listing = true;
        Targets.Clear();
        if (screens.Count > 1)
        {
            Targets.Add(new SiteTarget("Dashboard browser, on another screen", null, false));
            foreach (var screen in screens) Targets.Add(new SiteTarget($"Dashboard browser on {ScreenName(screen)}", screen.DeviceName, false));
        }
        else
        {
            Targets.Add(new SiteTarget("Dashboard browser", null, false));
        }
        Targets.Add(new SiteTarget(defaultBrowser, null, true));

        SelectedTarget = _inDefaultBrowser
            ? Targets[^1]
            : Targets.FirstOrDefault(t => !t.IsDefaultBrowser && t.Screen != null && t.Screen == _screen) ?? Targets[0];
        _listing = false;
    }

    // Windows numbers its screens by their device name (\\.\DISPLAY2 is 2), as its display settings show them.
    private static string ScreenName(Screen screen)
    {
        var number = new string(screen.DeviceName.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        var name = number.Length > 0 ? $"screen {number}" : screen.DeviceName;
        return $"{name}, {screen.Bounds.Width}×{screen.Bounds.Height}{(screen.Primary ? ", main" : string.Empty)}";
    }

    partial void OnSelectedTargetChanged(SiteTarget? value)
    {
        if (_listing || value == null) return;

        _inDefaultBrowser = value.IsDefaultBrowser;
        if (!value.IsDefaultBrowser) _screen = value.Screen;
        _save(_screen, _inDefaultBrowser);
        if (!value.IsDefaultBrowser) ScreenPicked?.Invoke(value.Screen);
    }

    /// <summary>
    /// Shows a project's site where the picker says, or where <paramref name="inDefaultBrowser"/> says just this once;
    /// <paramref name="activate"/> brings its browser window to the front.
    /// </summary>
    public void Open(string projectPath, string projectName, string url, bool activate, bool? inDefaultBrowser = null)
    {
        if (inDefaultBrowser ?? _inDefaultBrowser)
        {
            DefaultBrowser.Open(url);
            return;
        }
        WindowRequested?.Invoke(new SiteRequest(projectPath, projectName, url, WindowScreen(), activate));
    }

    /// <summary>
    /// The dashboard's header: the browser of no project's, to type an address or open a saved site without running
    /// anything. It's the dashboard browser whatever Open in says, as that's what the button is.
    /// </summary>
    [RelayCommand]
    private void OpenBrowser() => OpenWindow(null, null);

    /// <summary>Opens a project's browser window (or the one of no project's, for null) without a site, or brings it to the front.</summary>
    public void OpenWindow(string? projectPath, string? projectName) =>
        WindowRequested?.Invoke(new SiteRequest(projectPath, projectName, null, WindowScreen(), true));

    // With the default browser picked, the window goes where it went before that, if that screen is still here.
    private string? WindowScreen() => SelectedTarget is { IsDefaultBrowser: false } picked
        ? picked.Screen
        : Targets.FirstOrDefault(t => !t.IsDefaultBrowser && t.Screen != null && t.Screen == _screen)?.Screen;

    /// <summary>The saved site at this address, if there is one; http://localhost:3000 and http://localhost:3000/ are one.</summary>
    public SavedSite? FindSaved(string? url) =>
        url != null && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? SavedSites.FirstOrDefault(s => Uri.TryCreate(s.Url, UriKind.Absolute, out var saved) && SameSite(saved, uri))
            : null;

    private static bool SameSite(Uri a, Uri b) =>
        Uri.Compare(a, b, UriComponents.HttpRequestUrl, UriFormat.SafeUnescaped, StringComparison.Ordinal) == 0;

    /// <summary>Saves the site at the end of the bar, or gives back the one already saved at its address.</summary>
    public SavedSite Save(string url, string name)
    {
        if (FindSaved(url) is { } saved) return saved;
        var site = new SavedSite { Name = name, Url = url };
        SavedSites.Add(site);
        Persist();
        return site;
    }

    /// <summary>Renames a saved site, or gives it another address, in its place in the bar; gives back the edited one.</summary>
    public SavedSite Update(SavedSite site, string name, string url)
    {
        var index = SavedSites.IndexOf(site);
        if (index < 0 || (site.Name == name && site.Url == url)) return site;
        // A new one rather than changing it, so the bars and start pages showing it redraw it.
        var edited = new SavedSite { Name = name, Url = url };
        SavedSites[index] = edited;
        Persist();
        return edited;
    }

    public void Remove(SavedSite site)
    {
        if (SavedSites.Remove(site)) Persist();
    }

    private void Persist() => _saveSites(SavedSites.ToList());
}
