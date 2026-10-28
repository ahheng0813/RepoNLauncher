namespace RepoNLauncher.Models;

public class DeviceInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public bool IsEmulator { get; set; }

    public override string ToString() => $"{Name} ({Platform})";
}
