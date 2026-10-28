using System.Text.Json;
using System.Text.Json.Serialization;
using RepoNLauncher.Models;

namespace RepoNLauncher.Services;

public interface IFlutterService
{
    Task<List<DeviceInfo>> GetDevicesAsync(CancellationToken ct = default);
    string BuildRunCommand(string deviceId);
}

public class FlutterService : IFlutterService
{
    private readonly IProcessRunner _runner;

    public FlutterService(IProcessRunner runner) => _runner = runner;

    public async Task<List<DeviceInfo>> GetDevicesAsync(CancellationToken ct = default)
    {
        // Run from the user's home directory; device listing isn't project-specific.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = await _runner.RunCaptureAsync(home, "flutter devices --machine", ct);

        var devices = new List<DeviceInfo>();
        if (string.IsNullOrWhiteSpace(result.StdOut)) return devices;

        try
        {
            var dtos = JsonSerializer.Deserialize<List<FlutterDeviceDto>>(result.StdOut);
            if (dtos == null) return devices;

            foreach (var dto in dtos)
            {
                devices.Add(new DeviceInfo
                {
                    Id = dto.Id ?? string.Empty,
                    Name = dto.Name ?? dto.Id ?? "Unknown device",
                    Platform = dto.TargetPlatform ?? dto.Platform ?? "unknown",
                    IsEmulator = dto.Emulator,
                });
            }
        }
        catch (JsonException)
        {
            // `flutter devices --machine` prints non-JSON noise (e.g. a slow-start warning)
            // before the array on some setups; nothing usable to parse in that case.
        }

        return devices;
    }

    public string BuildRunCommand(string deviceId) =>
        string.IsNullOrWhiteSpace(deviceId) ? "flutter run" : $"flutter run -d {deviceId}";

    private class FlutterDeviceDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("targetPlatform")]
        public string? TargetPlatform { get; set; }

        [JsonPropertyName("platform")]
        public string? Platform { get; set; }

        [JsonPropertyName("emulator")]
        public bool Emulator { get; set; }
    }
}
