using System.Net;
using System.Net.Http;

namespace RepoNLauncher.Services;

/// <summary>
/// How the app talks to OpenStreetMap's servers for the Location map and place search. Their usage policies ask every app
/// to name itself, with a way to reach whoever makes it, rather than send an HTTP library's default identity.
/// </summary>
public static class OpenStreetMap
{
    public const string CopyrightUrl = "https://www.openstreetmap.org/copyright";

    public static HttpClient Http { get; } = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(15),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        var version = typeof(OpenStreetMap).Assembly.GetName().Version?.ToString(3) ?? "1.0";
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"RepoNLauncher/{version} (+https://github.com/ahheng0813/RepoNLauncher)");
        return http;
    }
}
