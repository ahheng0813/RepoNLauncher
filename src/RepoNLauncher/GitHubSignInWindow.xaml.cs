using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Navigation;
using RepoNLauncher.Services;

namespace RepoNLauncher;

/// <summary>Signs in to GitHub through Git's credential manager or with a pasted token. DialogResult is true once signed in.</summary>
public partial class GitHubSignInWindow : Window
{
    private readonly IGitHubService _gitHub;
    private CancellationTokenSource? _signIn;

    public GitHubSignInWindow(IGitHubService gitHub)
    {
        InitializeComponent();
        _gitHub = gitHub;

        SourceInitialized += (_, _) => NativeMethods.UseImmersiveDarkMode(new WindowInteropHelper(this).Handle);
        // Closing mid-sign-in stops it, which also closes a Git Credential Manager window left open.
        Closing += (_, _) => _signIn?.Cancel();
    }

    private async void SignInWithGit_Click(object sender, RoutedEventArgs e) =>
        await SignInAsync(_gitHub.SignInWithGitAsync, "Waiting for GitHub. Finish signing in in the window or browser tab that opened.");

    private async void SignInWithToken_Click(object sender, RoutedEventArgs e)
    {
        var token = TokenBox.Password.Trim();
        if (token.Length == 0)
        {
            ShowStatus("Paste a token first.", isError: true);
            TokenBox.Focus();
            return;
        }
        await SignInAsync(ct => _gitHub.SignInWithTokenAsync(token, ct), "Checking the token with GitHub…");
    }

    private void TokenBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SignInWithToken_Click(sender, e);
    }

    private async Task SignInAsync(Func<CancellationToken, Task<string>> signIn, string waitingText)
    {
        if (_signIn != null) return;

        using var cancel = _signIn = new CancellationTokenSource();
        SetBusy(true);
        ShowStatus(waitingText, isError: false);
        try
        {
            await signIn(cancel.Token);
            if (IsVisible) DialogResult = true;
        }
        catch (GitHubException ex)
        {
            ShowStatus(ex.Message, isError: true);
        }
        catch (OperationCanceledException) { }
        finally
        {
            _signIn = null;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        GitSignInButton.IsEnabled = !busy;
        TokenSignInButton.IsEnabled = !busy;
        TokenBox.IsEnabled = !busy;
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowStatus(string text, bool isError)
    {
        StatusPanel.Visibility = Visibility.Visible;
        StatusText.Text = text;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource(isError ? "Danger" : "Text.Secondary");
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
