using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Forms;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RepoNLauncher.ViewModels;

/// <summary>
/// A choice in the Run tab's Open in picker: the browser window on a screen (a null <see cref="Screen"/> picks another
/// than the dashboard's when it opens), or the PC's default browser.
/// </summary>
public sealed class SiteTarget(string label, string? screen, bool isDefaultBrowser)
{
    public string Label { get; } = label;

    /// <summary>The screen's device name, such as \\.\DISPLAY2.</summary>
    public string? Screen { get; } = screen;
    public bool IsDefaultBrowser { get; } = isDefaultBrowser;
    public override string ToString() => Label;
}

/// <summary>A project's site to show in its browser window, on <see cref="Screen"/>; <see cref="Activate"/> brings it to the front.</summary>
public sealed record SiteRequest(string ProjectPath, string ProjectName, string Url, string? Screen, bool Activate);

/// <summary>
/// Where the Run tab opens the site a project serves: the dashboard's own browser window, on a screen of its own
/// (another than the dashboard's by default), or the PC's default browser. One choice for every project.
/// </summary>
public partial class SiteBrowserViewModel : ObservableObject
{
    private readonly Action<string?, bool> _save;

    // What's saved, kept while the screen is unplugged so picking it comes back when it is again.
    private string? _screen;
    private bool _inDefaultBrowser;

    // The screens the picker lists, so opening it only lists them again when they changed.
    private string _listedScreens = string.Empty;
    private bool _listing;

    public ObservableCollection<SiteTarget> Targets { get; } = new();

    [ObservableProperty] private SiteTarget? _selectedTarget;

    /// <summary>Raised to show a site in the browser window; the dashboard opens one, or uses the project's open one.</summary>
    public event Action<SiteRequest>? WindowRequested;

    /// <summary>Raised when another screen is picked, so the browser windows open now move to it.</summary>
    public event Action<string?>? ScreenPicked;

    public SiteBrowserViewModel(string? screen, bool inDefaultBrowser, Action<string?, bool> save)
    {
        _screen = screen;
        _inDefaultBrowser = inDefaultBrowser;
        _save = save;
        ListTargets();
    }

    /// <summary>Lists the screens again, such as when the picker opens, so one plugged in a moment ago is there.</summary>
    public void ListTargets()
    {
        var screens = Screen.AllScreens.OrderBy(s => s.Bounds.Left).ThenBy(s => s.Bounds.Top).ToList();
        var listed = string.Join("|", screens.Select(s => $"{s.DeviceName} {s.Bounds}"));
        if (listed == _listedScreens) return;
        _listedScreens = listed;

        _listing = true;
        Targets.Clear();
        if (screens.Count > 1)
        {
            Targets.Add(new SiteTarget("Browser window, on another screen", null, false));
            foreach (var screen in screens) Targets.Add(new SiteTarget($"Browser window on {ScreenName(screen)}", screen.DeviceName, false));
        }
        else
        {
            Targets.Add(new SiteTarget("Browser window", null, false));
        }
        Targets.Add(new SiteTarget("Default browser", null, true));

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

    /// <summary>Shows a project's site where the picker says; <paramref name="activate"/> brings its browser window to the front.</summary>
    public void Open(string projectPath, string projectName, string url, bool activate)
    {
        if (_inDefaultBrowser)
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return;
        }
        WindowRequested?.Invoke(new SiteRequest(projectPath, projectName, url, SelectedTarget?.Screen, activate));
    }
}
