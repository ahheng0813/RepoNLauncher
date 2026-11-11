namespace RepoNLauncher.Models;

/// <summary>How the dashboard shows dates and times, picked in Settings. A null pattern follows Windows' own format.</summary>
public class DateTimeFormatSettings
{
    /// <summary>A .NET date pattern such as yyyy-MM-dd; null for Windows' short date.</summary>
    public string? DatePattern { get; set; }

    /// <summary>A .NET time pattern such as HH:mm; null for Windows' short time.</summary>
    public string? TimePattern { get; set; }
}
