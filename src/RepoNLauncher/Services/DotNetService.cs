using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RepoNLauncher.Services;

public interface IDotNetService
{
    string? FindCsproj(string projectPath);
    /// <summary>The shallowest .sln/.slnx in the project (up to two folders down), or null if there is none.</summary>
    string? FindSolution(string projectPath);
    string BuildRunCommand(string csprojPath);
    /// <summary>Ports from the applicationUrl of the launch profile <see cref="BuildRunCommand"/> runs.</summary>
    List<int> GetLaunchPorts(string csprojPath);
    string? TryExtractListeningUrl(string outputLine);
}

public class DotNetService : IDotNetService
{
    private static readonly Regex ListeningRegex = new(
        @"Now listening on:\s*(?<url>https?://\S+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string? FindCsproj(string projectPath)
    {
        if (!Directory.Exists(projectPath)) return null;

        var topLevel = Directory.EnumerateFiles(projectPath, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (topLevel != null) return topLevel;

        return Directory.EnumerateFiles(projectPath, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                        !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .FirstOrDefault();
    }

    // Some repos keep a solution at the root and another for a sub-project one folder down
    // (APIGateway.slnx vs APIGateway/APIGateway.sln) — the shallowest one covers the whole repo.
    // The depth cap also keeps out the .sln files CMake generates under a Flutter app's build/windows.
    public string? FindSolution(string projectPath)
    {
        if (!Directory.Exists(projectPath)) return null;

        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2 };
        return Directory.EnumerateFiles(projectPath, "*.sln*", options)
            .Where(p => Path.GetExtension(p).Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
                        Path.GetExtension(p).Equals(".slnx", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Count(c => c == Path.DirectorySeparatorChar))
            .FirstOrDefault();
    }

    public string BuildRunCommand(string csprojPath)
    {
        var command = $"dotnet run --project \"{csprojPath}\"";
        var profile = ChooseLaunchProfile(csprojPath);
        return profile == null ? command : $"{command} --launch-profile \"{profile.Name}\"";
    }

    public List<int> GetLaunchPorts(string csprojPath)
    {
        var profile = ChooseLaunchProfile(csprojPath);
        if (profile == null) return new List<int>();

        // Wildcard hosts ("http://*:5000", "http://+:5000") aren't valid URIs, but the port is all we need.
        return profile.ApplicationUrl
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(u => Uri.TryCreate(u.Replace("*", "localhost").Replace("+", "localhost"), UriKind.Absolute, out var uri) ? uri.Port : -1)
            .Where(p => p > 0)
            .Distinct()
            .ToList();
    }

    private record LaunchProfile(string Name, string ApplicationUrl);

    // `dotnet run` uses the first "Project" profile in launchSettings.json, which in the
    // ASP.NET templates is the http-only one — so prefer a profile that also serves https.
    private static LaunchProfile? ChooseLaunchProfile(string csprojPath)
    {
        var profiles = ReadProjectLaunchProfiles(csprojPath);
        var httpsProfiles = profiles
            .Where(p => p.ApplicationUrl.Contains("https://", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return httpsProfiles.FirstOrDefault(p => p.Name.Equals("https", StringComparison.OrdinalIgnoreCase))
               ?? httpsProfiles.FirstOrDefault()
               ?? profiles.FirstOrDefault();
    }

    private static List<LaunchProfile> ReadProjectLaunchProfiles(string csprojPath)
    {
        var none = new List<LaunchProfile>();
        var projectDir = Path.GetDirectoryName(csprojPath);
        if (projectDir == null) return none;

        var launchSettings = Path.Combine(projectDir, "Properties", "launchSettings.json");
        if (!File.Exists(launchSettings)) return none;

        try
        {
            using var stream = File.OpenRead(launchSettings);
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (!doc.RootElement.TryGetProperty("profiles", out var profiles) ||
                profiles.ValueKind != JsonValueKind.Object)
                return none;

            return profiles.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.Object &&
                            p.Value.TryGetProperty("commandName", out var cmd) && cmd.GetString() == "Project")
                .Select(p => new LaunchProfile(
                    p.Name,
                    p.Value.TryGetProperty("applicationUrl", out var url) ? url.GetString() ?? string.Empty : string.Empty))
                .ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            return none;
        }
    }

    public string? TryExtractListeningUrl(string outputLine)
    {
        var match = ListeningRegex.Match(outputLine);
        return match.Success ? match.Groups["url"].Value : null;
    }
}
