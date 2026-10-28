using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using RepoNLauncher.Models;
using RepoNLauncher.ViewModels;

namespace RepoNLauncher;

/// <summary>Edits the user-added programs of the OPEN IN row. Works on a copy; <see cref="Result"/> is set only on Save.</summary>
public partial class OpenWithAppsWindow : Window
{
    private readonly ObservableCollection<OpenWithAppEditor> _apps;

    public OpenWithAppsWindow(IEnumerable<OpenWithApp> apps)
    {
        InitializeComponent();
        _apps = new ObservableCollection<OpenWithAppEditor>(apps.Select(a =>
            new OpenWithAppEditor { Name = a.Name, Program = a.Program, Arguments = a.Arguments }));
        AppList.ItemsSource = _apps;

        SourceInitialized += (_, _) => NativeMethods.UseImmersiveDarkMode(new WindowInteropHelper(this).Handle);
    }

    public List<OpenWithApp>? Result { get; private set; }

    // Adding goes straight to picking the program; a row left without one is dropped on Save.
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var app = new OpenWithAppEditor();
        _apps.Add(app);
        Browse(app);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is OpenWithAppEditor app) Browse(app);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is OpenWithAppEditor app) _apps.Remove(app);
    }

    private void Browse(OpenWithAppEditor app)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose the program",
            Filter = "Programs (*.exe;*.cmd;*.bat)|*.exe;*.cmd;*.bat|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;

        app.Program = dialog.FileName;
        if (string.IsNullOrWhiteSpace(app.Name)) app.Name = DisplayName(dialog.FileName);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Result = _apps
            .Where(a => !string.IsNullOrWhiteSpace(a.Program))
            .Select(a => new OpenWithApp
            {
                Program = a.Program.Trim(),
                Name = string.IsNullOrWhiteSpace(a.Name) ? DisplayName(a.Program.Trim()) : a.Name.Trim(),
                Arguments = a.Arguments.Trim(),
            })
            .ToList();
        DialogResult = true;
    }

    // Installed programs carry a friendly name (Code.exe says "Visual Studio Code"); otherwise use the file name.
    private static string DisplayName(string program)
    {
        try
        {
            var description = FileVersionInfo.GetVersionInfo(program).FileDescription?.Trim();
            if (!string.IsNullOrEmpty(description)) return description;
        }
        catch (Exception ex) when (ex is FileNotFoundException or ArgumentException) { }

        return Path.GetFileNameWithoutExtension(program);
    }
}
