using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace RepoNLauncher.Services;

/// <summary>Where this PC is, as Windows' location service knows it (from Wi-Fi, GPS or the network address).</summary>
public static class PcLocation
{
    // Windows' location API is a WinRT one, which .NET 8 only reaches with the Windows SDK projections (tens of MB in the
    // exe). Windows PowerShell can call it as it is, so it asks instead.
    private const string Script = """
        $ErrorActionPreference = 'Stop'
        Add-Type -AssemblyName System.Runtime.WindowsRuntime
        $null = [Windows.Devices.Geolocation.Geolocator, Windows.Devices.Geolocation, ContentType = WindowsRuntime]
        $asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
            $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } |
            Select-Object -First 1
        $access = $asTask.MakeGenericMethod([Windows.Devices.Geolocation.GeolocationAccessStatus]).Invoke($null, @([Windows.Devices.Geolocation.Geolocator]::RequestAccessAsync()))
        $null = $access.Wait(10000)
        if ($access.Result -ne 'Allowed') { 'denied'; exit }
        $locator = New-Object Windows.Devices.Geolocation.Geolocator
        $position = $asTask.MakeGenericMethod([Windows.Devices.Geolocation.Geoposition]).Invoke($null, @($locator.GetGeopositionAsync()))
        if (-not $position.Wait(20000)) { 'timeout'; exit }
        $point = $position.Result.Coordinate.Point.Position
        [string]::Format([cultureinfo]::InvariantCulture, 'at {0} {1}', $point.Latitude, $point.Longitude)
        """;

    /// <summary>Throws with what to do when Windows won't say, e.g. because location is off for desktop apps.</summary>
    public static async Task<(double Latitude, double Longitude)> GetAsync(CancellationToken ct = default)
    {
        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
                     Convert.ToBase64String(Encoding.Unicode.GetBytes(Script)) })
            info.ArgumentList.Add(arg);

        string output, errors;
        try
        {
            using var process = Process.Start(info)!;
            var errorsTask = process.StandardError.ReadToEndAsync(ct);
            output = await process.StandardOutput.ReadToEndAsync(ct);
            errors = await errorsTask;
            await process.WaitForExitAsync(ct);
        }
        catch (Win32Exception)
        {
            throw new InvalidOperationException("Windows PowerShell, which asks Windows for the location, isn't on this PC.");
        }

        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? string.Empty;
        var parts = line.Split(' ');
        if (parts.Length == 3 && parts[0] == "at"
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude)
            && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude))
            return (latitude, longitude);

        throw new InvalidOperationException(line switch
        {
            "denied" => "Windows won't share this PC's location. In Settings, Privacy & security, Location, turn on Location services and Let desktop apps access your location.",
            "timeout" => "Windows couldn't work out where this PC is. Try again in a moment, or type the coordinates.",
            _ => "Windows couldn't give this PC's location" + (errors.Trim().Length > 0 ? $": {errors.Trim().Split('\n')[0].Trim()}" : "."),
        });
    }
}
