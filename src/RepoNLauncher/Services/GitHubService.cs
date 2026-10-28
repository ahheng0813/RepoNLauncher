using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace RepoNLauncher.Services;

/// <summary>A GitHub repository as GitHub reports it now, after any rename or transfer.</summary>
public record GitHubRepo(string Owner, string FullName, bool IsPrivate);

/// <summary>A GitHub problem worth showing the user as is.</summary>
public class GitHubException(string message) : Exception(message);

public interface IGitHubService
{
    /// <summary>The signed-in account, or null when signed out. Nothing outside the GitHub features needs it.</summary>
    string? Login { get; }

    event Action? SignInChanged;

    /// <summary>Signs in with the GitHub sign-in Git uses. When Git has none yet, Git Credential Manager asks for one (in the browser).</summary>
    Task<string> SignInWithGitAsync(CancellationToken ct);

    Task<string> SignInWithTokenAsync(string token, CancellationToken ct);

    /// <summary>Checks the sign-in saved from last time still works, and signs out if GitHub no longer accepts it.</summary>
    Task VerifySavedSignInAsync();

    void SignOut();

    /// <summary>The repository, or null when it doesn't exist or this account can't see it.</summary>
    Task<GitHubRepo?> GetRepoAsync(string owner, string name, CancellationToken ct);
}

public class GitHubService : IGitHubService
{
    private const string ApiHost = "api.github.com";
    private const string GitCredentialRequest = "protocol=https\nhost=github.com\n\n";

    // Kept encrypted for this Windows user (DPAPI), apart from config.json so the settings file never holds the token.
    private static readonly string SavedSignInPath = Path.Combine(DashboardConfigService.AppDataDir, "github-sign-in.dat");

    // Redirects are followed by hand (see SendAsync), so the token goes along.
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        BaseAddress = new Uri($"https://{ApiHost}/"),
        Timeout = TimeSpan.FromSeconds(30),
    };

    private string? _token;

    public GitHubService()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("RepoNLauncher/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        LoadSavedSignIn();
    }

    public string? Login { get; private set; }

    public event Action? SignInChanged;

    public async Task<string> SignInWithGitAsync(CancellationToken ct)
    {
        var credential = await RunGitCredentialAsync("fill", GitCredentialRequest, ct);
        var login = await LoginForTokenAsync(TokenOf(credential), ct);
        if (login == null)
        {
            // Git had a sign-in GitHub no longer accepts. Drop it, as git itself does, so Git asks for a fresh one.
            await RunGitCredentialAsync("reject", Describe(credential), ct);
            credential = await RunGitCredentialAsync("fill", GitCredentialRequest, ct);
            login = await LoginForTokenAsync(TokenOf(credential), ct)
                    ?? throw new GitHubException("GitHub didn't accept the sign-in Git gave. Try a token instead.");
        }

        // Tells Git the sign-in worked, as it does after a successful fetch, so Git remembers one made just now.
        try { await RunGitCredentialAsync("approve", Describe(credential), ct); }
        catch (GitHubException) { }

        Save(login, TokenOf(credential));
        return login;
    }

    public async Task<string> SignInWithTokenAsync(string token, CancellationToken ct)
    {
        var login = await LoginForTokenAsync(token, ct)
                    ?? throw new GitHubException("GitHub didn't accept that token. Check it was copied whole and hasn't expired.");
        Save(login, token);
        return login;
    }

    public async Task VerifySavedSignInAsync()
    {
        if (_token == null) return;
        try
        {
            var login = await LoginForTokenAsync(_token, CancellationToken.None);
            if (login == null) SignOut();
            else if (login != Login) Save(login, _token); // the account was renamed
        }
        catch (GitHubException) { } // offline: keep the sign-in and check again next start
    }

    public void SignOut()
    {
        _token = null;
        Login = null;
        try { File.Delete(SavedSignInPath); }
        catch (IOException) { }
        SignInChanged?.Invoke();
    }

    public async Task<GitHubRepo?> GetRepoAsync(string owner, string name, CancellationToken ct)
    {
        var token = _token ?? throw new GitHubException("Sign in to GitHub first.");
        using var response = await SendAsync($"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}", token, ct);
        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized:
                SignOut();
                throw new GitHubException("GitHub no longer accepts your sign-in. Sign in again.");
            case HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests when IsRateLimited(response):
                throw new GitHubException("GitHub's hourly request limit is used up. Try again later.");
            // Forbidden otherwise means an organization blocks this sign-in (e.g. it requires SSO); 451 is a takedown.
            case HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.UnavailableForLegalReasons:
                return null;
        }

        var root = await ReadJsonAsync(response, ct);
        return new GitHubRepo(
            root.GetProperty("owner").GetProperty("login").GetString()!,
            root.GetProperty("full_name").GetString()!,
            root.GetProperty("private").GetBoolean());
    }

    // The login the token belongs to, or null when GitHub rejects the token.
    private async Task<string?> LoginForTokenAsync(string token, CancellationToken ct)
    {
        using var response = await SendAsync("user", token, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized) return null;
        return (await ReadJsonAsync(response, ct)).GetProperty("login").GetString();
    }

    private async Task<HttpResponseMessage> SendAsync(string path, string token, CancellationToken ct)
    {
        var uri = new Uri(_http.BaseAddress!, path);
        // A renamed or transferred repo answers with a redirect. HttpClient would follow it without the token,
        // which turns a private repo into a 404, so redirects within the API are followed here.
        for (var hops = 0; ; hops++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            HttpResponseMessage response;
            try { response = await _http.SendAsync(request, ct); }
            catch (HttpRequestException) { throw new GitHubException("Couldn't reach GitHub. Check the internet connection."); }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new GitHubException("GitHub took too long to answer."); }

            if (hops < 3 && (int)response.StatusCode is 301 or 302 or 307 or 308 && response.Headers.Location is { } location)
            {
                var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (next.Host == ApiHost)
                {
                    response.Dispose();
                    uri = next;
                    continue;
                }
            }
            return response;
        }
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            throw new GitHubException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return document.RootElement.Clone();
    }

    private static bool IsRateLimited(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.TooManyRequests ||
        (response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) && remaining.FirstOrDefault() == "0");

    // `git credential` speaks key=value lines. Terminal prompts are off: there's no terminal to answer them,
    // and without that git would wait forever when no credential manager is set up.
    private static async Task<Dictionary<string, string>> RunGitCredentialAsync(string action, string input, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo("git", $"credential {action}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

        Process process;
        try { process = Process.Start(startInfo)!; }
        catch (Win32Exception) { throw new GitHubException("Git isn't installed, so sign in with a token instead."); }

        using (process)
        {
            await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
            try { await process.WaitForExitAsync(ct); }
            catch (OperationCanceledException)
            {
                // Also closes the Git Credential Manager window the user walked away from.
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                throw;
            }

            if (process.ExitCode != 0)
            {
                Debug.WriteLine($"git credential {action}: {await errors}");
                throw new GitHubException("Git didn't sign in to GitHub (the sign-in was cancelled, or Git has no credential manager). You can use a token instead.");
            }

            return (await output)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.Split('=', 2))
                .Where(pair => pair.Length == 2)
                .GroupBy(pair => pair[0])
                .ToDictionary(group => group.Key, group => group.Last()[1]);
        }
    }

    private static string TokenOf(Dictionary<string, string> credential) =>
        credential.TryGetValue("password", out var token) && token.Length > 0
            ? token
            : throw new GitHubException("Git didn't return a GitHub sign-in. You can use a token instead.");

    private static string Describe(Dictionary<string, string> credential) =>
        GitCredentialRequest.TrimEnd('\n') + "\n" +
        string.Concat(credential.Where(c => c.Key is "username" or "password").Select(c => $"{c.Key}={c.Value}\n")) + "\n";

    private void Save(string login, string token)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new SavedSignIn(login, token));
        Directory.CreateDirectory(DashboardConfigService.AppDataDir);
        File.WriteAllBytes(SavedSignInPath, ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser));
        _token = token;
        Login = login;
        SignInChanged?.Invoke();
    }

    private void LoadSavedSignIn()
    {
        try
        {
            var json = ProtectedData.Unprotect(File.ReadAllBytes(SavedSignInPath), null, DataProtectionScope.CurrentUser);
            if (JsonSerializer.Deserialize<SavedSignIn>(json) is not { } saved) return;
            _token = saved.Token;
            Login = saved.Login;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException) { }
    }

    private sealed record SavedSignIn(string Login, string Token);
}
