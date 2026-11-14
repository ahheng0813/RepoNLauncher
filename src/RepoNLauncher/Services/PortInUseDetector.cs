using System.Text.RegularExpressions;

namespace RepoNLauncher.Services;

/// <summary>A port a run wanted and found taken, and the process holding it when the message names one.</summary>
public record PortConflict(int? Port, int? Pid);

/// <summary>
/// Reads a run's output a line at a time for the server saying the port it wanted is taken, and reports the first one.
/// Some servers move to another port and carry on (Next, Vite), some stop (Node's EADDRINUSE, Kestrel), and Next also
/// stops when another next dev of the same folder is running, whatever port that one is on.
/// </summary>
public sealed class PortInUseDetector
{
    // Next: "Port 3000 is in use by process 31712, using available port 3001 instead."
    // Vite: "Port 5173 is in use, trying another one..."; Flask: "Port 5000 is in use by another program."
    // Create React App: "Something is already running on port 3000."
    private static readonly Regex PortInUse = new(
        @"\bport (?<port>\d{2,5}) is (?:already )?in use(?: by process (?<pid>\d{1,9}))?|already running on port (?<port>\d{2,5})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Node: "Error: listen EADDRINUSE: address already in use :::3000"
    // Kestrel: "Failed to bind to address http://127.0.0.1:5000: address already in use."
    private static readonly Regex AddressInUse = new(@"EADDRINUSE|address already in use",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PortAfterColon = new(@":(?<port>\d{2,5})(?!\d)", RegexOptions.Compiled);

    // Next, when another next dev of the same folder is running:
    //   ⨯ Another next dev server is already running.
    //   - Local:        http://localhost:3000
    //   - PID:          31712
    private static readonly Regex AnotherServerRunning = new(@"\banother\b.*\bserver is already running",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LocalAddress = new(@"^\W*Local:\s+https?://[^\s/]+:(?<port>\d{2,5})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PidLine = new(@"^\W*PID:\s+(?<pid>\d{1,9})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // How far after Next's first line its address and PID can come.
    private const int DetailLines = 6;

    private bool _reported;
    private int _detailLinesLeft;
    private int? _detailPort;

    /// <summary>The conflict this line completes, the first time the output shows one; null otherwise.</summary>
    public PortConflict? Read(string line)
    {
        if (_reported) return null;
        var conflict = Match(line);
        _reported = conflict != null;
        return conflict;
    }

    private PortConflict? Match(string line)
    {
        if (_detailLinesLeft > 0)
        {
            _detailLinesLeft--;
            if (LocalAddress.Match(line) is { Success: true } local) _detailPort = Port(local);
            if (PidLine.Match(line) is { Success: true } pid) return new PortConflict(_detailPort, int.Parse(pid.Groups["pid"].Value));
            // No PID came: what listens on the address does.
            return _detailLinesLeft == 0 && _detailPort != null ? new PortConflict(_detailPort, null) : null;
        }

        if (AnotherServerRunning.IsMatch(line))
        {
            _detailLinesLeft = DetailLines;
            _detailPort = null;
            return null;
        }

        if (PortInUse.Match(line) is { Success: true } inUse && Port(inUse) is { } port)
            return new PortConflict(port, inUse.Groups["pid"].Success ? int.Parse(inUse.Groups["pid"].Value) : null);

        if (AddressInUse.IsMatch(line) && PortAfterColon.Match(line) is { Success: true } address && Port(address) is { } boundPort)
            return new PortConflict(boundPort, null);

        return null;
    }

    private static int? Port(Match match) =>
        int.TryParse(match.Groups["port"].Value, out var port) && port is > 0 and <= 65535 ? port : null;
}
