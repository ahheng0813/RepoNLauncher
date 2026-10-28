using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using RepoNLauncher.Models;

namespace RepoNLauncher.Services;

public interface IEnvironmentParser
{
    List<EnvEntry> ParseDotNetProject(string projectPath, string? csprojPath);
    List<EnvEntry> ParseFlutterProject(string projectPath, string? envFileOverride);
}

public class EnvironmentParserService : IEnvironmentParser
{
    private static readonly string[] HighlightKeywords =
    {
        "url", "endpoint", "host", "connectionstring", "baseaddress", "port", "uri"
    };

    private static readonly Regex DartConstRegex = new(
        @"(?i)\b(base_?url|api_?url|endpoint|host)\w*\s*=\s*['""]([^'""]+)['""]",
        RegexOptions.Compiled);

    public List<EnvEntry> ParseDotNetProject(string projectPath, string? csprojPath)
    {
        var entries = new List<EnvEntry>();
        var baseDir = csprojPath != null ? Path.GetDirectoryName(csprojPath) ?? projectPath : projectPath;

        foreach (var fileName in new[] { "appsettings.json", "appsettings.Development.json" })
        {
            var filePath = Path.Combine(baseDir, fileName);
            if (!File.Exists(filePath)) continue;

            try
            {
                using var stream = File.OpenRead(filePath);
                using var doc = JsonDocument.Parse(stream);
                FlattenJson(doc.RootElement, fileName, "", entries);
            }
            catch (JsonException)
            {
                // malformed settings file; skip rather than crash the UI
            }
        }

        return entries;
    }

    public List<EnvEntry> ParseFlutterProject(string projectPath, string? envFileOverride)
    {
        var entries = new List<EnvEntry>();

        var envPath = envFileOverride ?? Path.Combine(projectPath, ".env");
        if (File.Exists(envPath))
        {
            foreach (var rawLine in File.ReadAllLines(envPath))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;

                var idx = line.IndexOf('=');
                if (idx <= 0) continue;

                var key = line[..idx].Trim();
                var value = line[(idx + 1)..].Trim().Trim('"');
                entries.Add(new EnvEntry
                {
                    Source = ".env",
                    Key = key,
                    Value = value,
                    IsHighlighted = IsHighlighted(key, value),
                });
            }
        }

        var libDir = Path.Combine(projectPath, "lib");
        if (Directory.Exists(libDir))
        {
            IEnumerable<string> dartFiles;
            try
            {
                dartFiles = Directory.EnumerateFiles(libDir, "*.dart", SearchOption.AllDirectories);
            }
            catch (IOException)
            {
                dartFiles = Enumerable.Empty<string>();
            }

            foreach (var file in dartFiles)
            {
                string[] lines;
                try { lines = File.ReadAllLines(file); }
                catch (IOException) { continue; }

                foreach (var line in lines)
                {
                    var match = DartConstRegex.Match(line);
                    if (!match.Success) continue;

                    entries.Add(new EnvEntry
                    {
                        Source = Path.GetRelativePath(projectPath, file),
                        Key = match.Groups[1].Value,
                        Value = match.Groups[2].Value,
                        IsHighlighted = true,
                    });
                }
            }
        }

        return entries;
    }

    private static void FlattenJson(JsonElement element, string source, string prefix, List<EnvEntry> entries)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    var key = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}:{prop.Name}";
                    FlattenJson(prop.Value, source, key, entries);
                }
                break;

            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in element.EnumerateArray())
                {
                    FlattenJson(item, source, $"{prefix}[{i}]", entries);
                    i++;
                }
                break;

            default:
                var value = element.ToString();
                entries.Add(new EnvEntry
                {
                    Source = source,
                    Key = prefix,
                    Value = value,
                    IsHighlighted = IsHighlighted(prefix, value),
                });
                break;
        }
    }

    private static bool IsHighlighted(string key, string value) =>
        HighlightKeywords.Any(k => key.Contains(k, StringComparison.OrdinalIgnoreCase)) ||
        value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}
