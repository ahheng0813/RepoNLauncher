using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RepoNLauncher.Services;

/// <summary>
/// Ends a process and every process under it. Process.Kill(entireProcessTree: true) ends each process before it looks
/// for that one's children, and Node ends the processes it started when it ends: stopping `npm run dev` ends npm,
/// which takes the shell running the script down with it, and by the time .NET looks under npm the shell is gone,
/// so the `next dev` beneath it, now without a parent, is never found and keeps the port. Here the whole tree is
/// found and opened first, while every link is still there, and only then ended.
/// </summary>
public static class ProcessTree
{
    private const int ProcessTerminate = 0x0001;
    private const int ProcessQueryLimitedInformation = 0x1000;
    private const uint SnapProcesses = 0x2;

    /// <summary>
    /// Ends <paramref name="root"/> and everything under it. Children it left behind are found even after it has
    /// ended, as long as <paramref name="root"/> is the Process that started it, which keeps its id from being reused.
    /// </summary>
    public static void Kill(Process root)
    {
        var opened = new List<SafeProcessHandle>();
        try
        {
            // Each process looked under, with when it started: a process naming one as its parent but older than it
            // was the child of an earlier process that had the same id.
            var parents = new Dictionary<int, long>();
            try
            {
                if (GetProcessTimes(root.SafeHandle, out var rootStarted, out _, out _, out _))
                    parents[root.Id] = rootStarted;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }

            if (parents.Count == 0)
            {
                // Its start time can't be read, so its children can't be told apart: fall back to .NET's own walk.
                try { root.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or AggregateException) { }
                return;
            }

            OpenDescendants(parents, opened);

            try { root.Kill(); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }

            // A process can start another between the look and its own ending: look twice more under those just ended.
            var ended = 0;
            for (var pass = 0; ended < opened.Count; pass++)
            {
                for (var i = ended; i < opened.Count; i++) TerminateProcess(opened[i], -1);
                ended = opened.Count;
                if (pass < 2) OpenDescendants(parents, opened);
            }
        }
        finally
        {
            foreach (var handle in opened) handle.Dispose();
        }
    }

    // Opens every process under one in parents that isn't in it yet, parents before their children, and adds them to
    // both. Holding the handles keeps their ids from being reused until all of them have been ended.
    private static void OpenDescendants(Dictionary<int, long> parents, List<SafeProcessHandle> opened)
    {
        var processes = Snapshot();
        bool grew;
        do
        {
            grew = false;
            foreach (var (id, parentId) in processes)
            {
                if (parents.ContainsKey(id) || !parents.TryGetValue(parentId, out var parentStarted)) continue;

                var handle = OpenProcess(ProcessTerminate | ProcessQueryLimitedInformation, false, id);
                if (handle.IsInvalid || !GetProcessTimes(handle, out var started, out _, out _, out _) || started < parentStarted)
                {
                    handle.Dispose();
                    continue;
                }
                opened.Add(handle);
                parents[id] = started;
                grew = true;
            }
        } while (grew);
    }

    private static List<(int Id, int ParentId)> Snapshot()
    {
        var result = new List<(int, int)>();
        using var snapshot = CreateToolhelp32Snapshot(SnapProcesses, 0);
        if (snapshot.IsInvalid) return result;

        var entry = new ProcessEntry32 { Size = Marshal.SizeOf<ProcessEntry32>() };
        for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
            result.Add((entry.ProcessId, entry.ParentProcessId));
        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public int Size;
        public int Usage;
        public int ProcessId;
        public IntPtr DefaultHeapId;
        public int ModuleId;
        public int Threads;
        public int ParentProcessId;
        public int PriorityClassBase;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(int access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(SafeProcessHandle process, int exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
}
