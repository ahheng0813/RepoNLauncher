using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using RepoNLauncher.ViewModels;
using Application = System.Windows.Application;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Point = System.Windows.Point;

namespace RepoNLauncher;

/// <summary>
/// A project's Run console in a window of its own, to keep its output in view on another screen while the dashboard
/// shows something else. One window per project, and any number of projects' at once; the Run tab keeps showing the
/// same output.
/// </summary>
public partial class RunConsoleWindow : Window
{
    private static readonly Dictionary<ProjectViewModel, RunConsoleWindow> OpenWindows = new();

    // Where each project's window was when it closed, so opening it again puts it back there (until the app closes).
    private static readonly Dictionary<string, (Rect Bounds, bool Maximized)> LastPlaces = new(StringComparer.OrdinalIgnoreCase);

    // Each further window on a screen opens this much down and to the right of the one before, rather than on top of it.
    private const double CascadeStep = 32;

    private bool _maximizeOnceMade;

    private RunConsoleWindow(ProjectViewModel project)
    {
        InitializeComponent();
        DataContext = project;
        Title = $"{project.Name} - Run - Repo'N'Launcher";

        SourceInitialized += (_, _) =>
        {
            NativeMethods.UseImmersiveDarkMode(new WindowInteropHelper(this).Handle);
            if (_maximizeOnceMade) WindowState = WindowState.Maximized;
        };
        Loaded += (_, _) => NextScreenButton.Visibility = Screen.AllScreens.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        Closing += (_, _) => LastPlaces[project.Path] = (
            WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds,
            WindowState == WindowState.Maximized);
        Closed += (_, _) =>
        {
            OpenWindows.Remove(project);
            // The console lets go of the project's output.
            DataContext = null;
        };
    }

    /// <summary>Shows the project's console window, opening one if it has none.</summary>
    public static void Open(ProjectViewModel project)
    {
        if (OpenWindows.TryGetValue(project, out var open))
        {
            if (open.WindowState == WindowState.Minimized) SystemCommands.RestoreWindow(open);
            open.Activate();
            return;
        }

        var window = new RunConsoleWindow(project);
        OpenWindows[project] = window;
        window.PlaceFirst(project.Path);
        window.Show();
    }

    /// <summary>Closes every console window; they aren't the dashboard's own, so they'd keep the app open.</summary>
    public static void CloseAll()
    {
        foreach (var window in OpenWindows.Values.ToList()) window.Close();
    }

    /// <summary>Closes the windows of projects taken off the dashboard.</summary>
    public static void CloseRemoved(IEnumerable<ProjectViewModel> projects)
    {
        var listed = projects.ToHashSet();
        foreach (var (project, window) in OpenWindows.ToList())
            if (!listed.Contains(project)) window.Close();
    }

    // Where it was last time, while that's still on a screen. Otherwise on a screen other than the dashboard's, as the
    // reason to open it is to see it beside the dashboard; with one screen, on the dashboard's.
    private void PlaceFirst(string projectPath)
    {
        if (LastPlaces.TryGetValue(projectPath, out var last) && IsOnAScreen(last.Bounds))
        {
            Left = last.Bounds.Left;
            Top = last.Bounds.Top;
            Width = last.Bounds.Width;
            Height = last.Bounds.Height;
            _maximizeOnceMade = last.Maximized;
            return;
        }

        var dashboard = DashboardScreen();
        var screen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName != dashboard.DeviceName) ?? dashboard;
        var area = WorkingArea(screen);
        Width = Math.Min(Width, area.Width * 0.9);
        Height = Math.Min(Height, area.Height * 0.9);

        var others = OpenWindows.Values.Count(w => w != this && ScreenOf(w).DeviceName == screen.DeviceName);
        var step = CascadeStep * (others % 8);
        Left = Math.Min(area.Left + (area.Width - Width) / 2 + step, area.Right - Width);
        Top = Math.Min(area.Top + (area.Height - Height) / 2 + step, area.Bottom - Height);
    }

    private static bool IsOnAScreen(Rect bounds)
    {
        // Its title bar, enough of it to drag it by.
        var titleBar = new Rect(bounds.Left, bounds.Top, bounds.Width, 40);
        return Screen.AllScreens.Any(s => WorkingArea(s).IntersectsWith(titleBar));
    }

    private static Screen DashboardScreen() => ScreenOf(Application.Current.MainWindow);

    private static Screen ScreenOf(Window window) => Screen.FromHandle(new WindowInteropHelper(window).Handle);

    // The screen's working area is in device pixels; the window's place in the dashboard's units.
    private static Rect WorkingArea(Screen screen)
    {
        var fromDevice = PresentationSource.FromVisual(Application.Current.MainWindow)?.CompositionTarget?.TransformFromDevice
                         ?? Matrix.Identity;
        var area = screen.WorkingArea;
        return new Rect(fromDevice.Transform(new Point(area.Left, area.Top)), fromDevice.Transform(new Vector(area.Width, area.Height)));
    }

    // To the next screen, centred there; a maximized window fills that one instead.
    private void NextScreen_Click(object sender, RoutedEventArgs e)
    {
        var screens = Screen.AllScreens.OrderBy(s => s.Bounds.Left).ThenBy(s => s.Bounds.Top).ToList();
        var index = screens.FindIndex(s => s.DeviceName == ScreenOf(this).DeviceName);
        var area = WorkingArea(screens[(index + 1) % screens.Count]);

        var maximized = WindowState == WindowState.Maximized;
        WindowState = WindowState.Normal;
        Width = Math.Min(Width, area.Width);
        Height = Math.Min(Height, area.Height);
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;
        if (maximized) WindowState = WindowState.Maximized;
    }

    private void Pin_Click(object sender, RoutedEventArgs e) => Topmost = !Topmost;

    // Ctrl+F searches the output wherever the focus is in the window.
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F || Keyboard.Modifiers != ModifierKeys.Control) return;
        RunOutput.FocusSearch();
        e.Handled = true;
    }
}
