using System.IO;
using System.Text.Json;
using RepoNLauncher.Models;

namespace RepoNLauncher.Services;

public interface IDashboardConfigService
{
    DashboardConfig Load();
    void Save(DashboardConfig config);
}

public class DashboardConfigService : IDashboardConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

#if DEBUG
    // A build run from source keeps its own settings, so trying out changes never touches the published app's config.
    private const string AppDataFolderName = "RepoNLauncher-Dev";
#else
    private const string AppDataFolderName = "RepoNLauncher";
#endif

    /// <summary>Where the app keeps its settings (and the GitHub sign-in).</summary>
    public static string AppDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppDataFolderName);

    private readonly string _configPath;

    public DashboardConfigService()
    {
        Directory.CreateDirectory(AppDataDir);
        _configPath = Path.Combine(AppDataDir, "config.json");
    }

    public DashboardConfig Load()
    {
        if (!File.Exists(_configPath))
            return new DashboardConfig();

        try
        {
            var json = File.ReadAllText(_configPath);
            return JsonSerializer.Deserialize<DashboardConfig>(json, JsonOptions) ?? new DashboardConfig();
        }
        catch (JsonException)
        {
            return new DashboardConfig();
        }
    }

    public void Save(DashboardConfig config)
    {
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(_configPath, json);
    }
}
