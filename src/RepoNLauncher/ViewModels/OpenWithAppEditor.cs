using CommunityToolkit.Mvvm.ComponentModel;
using RepoNLauncher.Models;

namespace RepoNLauncher.ViewModels;

/// <summary>One editable row of the Open-in apps dialog; becomes an <see cref="OpenWithApp"/> when saved.</summary>
public partial class OpenWithAppEditor : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _program = string.Empty;
    [ObservableProperty] private string _arguments = OpenWithApp.DefaultArguments;
}
