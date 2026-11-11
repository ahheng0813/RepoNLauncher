using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RepoNLauncher.Models;
using RepoNLauncher.Services;

namespace RepoNLauncher.ViewModels;

/// <summary>
/// One of a project's runs, with a console of its own. A Flutter project has one per device it's run on, each a tab over
/// the Run tab's console, so the app can run on several devices at once with Hot reload and Stop for each. Other projects
/// have the one.
/// </summary>
public sealed partial class RunSession(DeviceInfo? device, string title) : ObservableObject
{
    /// <summary>The device it runs on; null for a run that isn't on a device (dotnet run, a dev server).</summary>
    public DeviceInfo? Device { get; } = device;

    /// <summary>Its tab's name: the device's.</summary>
    public string Title { get; } = title;

    public string ToolTip => (Device == null ? Title : $"{Device.Name} ({Device.Platform}), {Device.Id}")
                             + (IsRunning ? ": running" : ": stopped");

    public ObservableCollection<string> ConsoleOutput { get; } = new();

    /// <summary>Addresses the app printed that it serves on.</summary>
    public ObservableCollection<string> DetectedUrls { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTip))]
    private bool _isRunning;

    /// <summary>Restart has stopped it and is waiting for it to end before running it again.</summary>
    [ObservableProperty] private bool _isRestarting;

    /// <summary>When it was last started, so the Phone screen's Console shows the latest run on a device.</summary>
    public DateTime StartedAt { get; internal set; }

    internal RunningProcessHandle? Process { get; set; }

    /// <summary>The command it last ran and the ports that frees first, so Restart runs the same again; null before it has run.</summary>
    internal string? Command { get; set; }
    internal List<int> Ports { get; set; } = [];

    // Counts its starts. A process stopped just before the tab was run again can still have output and its exit to come,
    // which belong to that run, not this one's console.
    internal int Generation { get; set; }

    // The console's last line is the start of one still being written, such as a prompt waiting for an answer.
    private bool _lastLineIsPartial;

    /// <summary>A new run in the tab starts from an empty console.</summary>
    internal void Clear()
    {
        ConsoleOutput.Clear();
        DetectedUrls.Clear();
        _lastLineIsPartial = false;
    }

    /// <summary>A line of the dashboard's own, shown in its own color.</summary>
    internal void AddDashboardLine(string text)
    {
        ConsoleOutput.Add($"[dashboard] {text}");
        _lastLineIsPartial = false;
    }

    // A partial line stays the console's last line, and the next output replaces it with more of the same line.
    internal void ShowOutput(string line, bool partial)
    {
        if (_lastLineIsPartial) ConsoleOutput[ConsoleOutput.Count - 1] = line;
        else ConsoleOutput.Add(line);
        _lastLineIsPartial = partial;
    }

    // What was sent to the app, shown as a terminal echoes it: after the prompt it answers, else on its own line.
    internal void Echo(string text)
    {
        if (_lastLineIsPartial) ConsoleOutput[ConsoleOutput.Count - 1] += text;
        else ConsoleOutput.Add(text);
        _lastLineIsPartial = false;
    }
}
