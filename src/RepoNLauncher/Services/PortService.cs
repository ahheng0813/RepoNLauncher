using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RepoNLauncher.Services;

public record PortListener(int Port, int Pid, string ProcessName);

public interface IPortService
{
    /// <summary>Returns the processes currently listening on any of the given TCP ports.</summary>
    List<PortListener> FindListeners(IEnumerable<int> ports);

    bool TryKillProcess(int pid, out string error);
}

/// <summary>
/// Reads the OS TCP listener table directly (GetExtendedTcpTable) rather than parsing
/// `netstat -ano`, whose state column is localized on non-English Windows.
/// </summary>
public class PortService : IPortService
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidListener = 3;
    private const int ErrorInsufficientBuffer = 122;

    // Row layouts of MIB_TCPROW_OWNER_PID / MIB_TCP6ROW_OWNER_PID; the table starts with a 4-byte entry count.
    private const int Ipv4RowSize = 24, Ipv4PortOffset = 8, Ipv4PidOffset = 20;
    private const int Ipv6RowSize = 56, Ipv6PortOffset = 20, Ipv6PidOffset = 52;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, int reserved);

    public List<PortListener> FindListeners(IEnumerable<int> ports)
    {
        var wanted = ports.ToHashSet();
        if (wanted.Count == 0) return new List<PortListener>();

        return ReadListeners(AfInet, Ipv4RowSize, Ipv4PortOffset, Ipv4PidOffset)
            .Concat(ReadListeners(AfInet6, Ipv6RowSize, Ipv6PortOffset, Ipv6PidOffset))
            .Where(l => wanted.Contains(l.Port) && l.Pid > 4) // 0 = idle, 4 = System (http.sys) — neither can be killed
            .Distinct()
            .Select(l => new PortListener(l.Port, l.Pid, GetProcessName(l.Pid)))
            .OrderBy(l => l.Port)
            .ToList();
    }

    public bool TryKillProcess(int pid, out string error)
    {
        error = string.Empty;
        if (pid == Environment.ProcessId)
        {
            error = "refusing to stop the dashboard itself";
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(3000); // let the OS release the socket before the new run binds it
            return true;
        }
        catch (ArgumentException)
        {
            return true; // already gone
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string GetProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return "unknown";
        }
    }

    private static List<(int Port, int Pid)> ReadListeners(int addressFamily, int rowSize, int portOffset, int pidOffset)
    {
        var result = new List<(int Port, int Pid)>();
        var size = 0;
        var status = GetExtendedTcpTable(IntPtr.Zero, ref size, false, addressFamily, TcpTableOwnerPidListener, 0);

        // The table can grow between the size query and the read, so retry a few times on ERROR_INSUFFICIENT_BUFFER.
        for (var attempt = 0; attempt < 3 && status == ErrorInsufficientBuffer; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                status = GetExtendedTcpTable(buffer, ref size, false, addressFamily, TcpTableOwnerPidListener, 0);
                if (status != 0) continue;

                var count = Marshal.ReadInt32(buffer);
                for (var i = 0; i < count; i++)
                {
                    var row = buffer + 4 + i * rowSize;
                    var rawPort = Marshal.ReadInt32(row + portOffset);
                    var port = ((rawPort & 0xFF) << 8) | ((rawPort >> 8) & 0xFF); // network byte order
                    result.Add((port, Marshal.ReadInt32(row + pidOffset)));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return result;
    }
}
