using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoNLauncher.Services;

namespace RepoNLauncher.ViewModels;

/// <summary>The GitHub sign-in shown in the header. Only Repos on this PC needs it; everything else works signed out.</summary>
public partial class GitHubAccountViewModel : ObservableObject
{
    public GitHubAccountViewModel(IGitHubService service)
    {
        Service = service;
        service.SignInChanged += () =>
        {
            OnPropertyChanged(nameof(Login));
            OnPropertyChanged(nameof(IsSignedIn));
        };
    }

    public IGitHubService Service { get; }

    public string? Login => Service.Login;

    public bool IsSignedIn => Login != null;

    [RelayCommand]
    private void SignOut() => Service.SignOut();
}
