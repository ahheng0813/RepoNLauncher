using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RepoNLauncher.ViewModels;

/// <summary>A collapsible section of the project list standing for one folder; see <see cref="ProjectTreeBuilder"/>.</summary>
public partial class ProjectGroupViewModel : ObservableObject
{
    private Action<ProjectGroupViewModel>? _expandedChanged;

    public ProjectGroupViewModel(string name, string path, IReadOnlyList<ProjectViewModel> projects, bool isExpanded,
        Action<ProjectGroupViewModel> expandedChanged)
    {
        Name = name;
        Path = path;
        Projects = projects;
        // Set before the callback is attached, so restoring the saved state doesn't count as the user toggling it.
        IsExpanded = isExpanded;
        _expandedChanged = expandedChanged;
    }

    public string Name { get; }

    /// <summary>The folder's full path; also the key its collapsed state is saved under.</summary>
    public string Path { get; }

    /// <summary>Repos anywhere in this section, subsections included.</summary>
    public IReadOnlyList<ProjectViewModel> Projects { get; }

    public int RepoCount => Projects.Count;

    /// <summary>
    /// The section's checkbox for Fetch Selected: true when all its repos are ticked, false when none are, null when
    /// only some are. Setting it ticks or unticks every repo in the section, subsections included; clicking the mixed
    /// state clears them, as the checkbox isn't three-state.
    /// </summary>
    public bool? IsChecked
    {
        get
        {
            var checkedCount = Projects.Count(p => p.IsChecked);
            return checkedCount == 0 ? false : checkedCount == Projects.Count ? true : null;
        }
        set
        {
            foreach (var project in Projects)
                project.IsChecked = value == true;
        }
    }

    /// <summary>Called when any repo's tick changes, since the section doesn't watch its repos itself.</summary>
    public void RefreshIsChecked() => OnPropertyChanged(nameof(IsChecked));

    /// <summary>The section's repos first, then its subsections (<see cref="ProjectViewModel"/> and <see cref="ProjectGroupViewModel"/> items).</summary>
    public ObservableCollection<object> Children { get; } = new();

    [ObservableProperty] private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value) => _expandedChanged?.Invoke(this);
}
