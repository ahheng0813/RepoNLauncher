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
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    // The processes given to EndWithApp and everything they start, even once the process that started one has ended.
    // Windows ends them all when this handle closes, which it does however the app ends: closed, restarted to update,
    // ended from Task Manager or crashed. Null when Windows wouldn't make one.
    private static readonly Lazy<SafeFileHandle?> AppJob = new(CreateAppJob);

    /// <summary>
    /// Ends <paramref name="process"/> and everything it goes on to start when the app ends. Without this a dev server
    /// outlives the app that ran it: the next run finds its port taken (Next.js: "Another next dev server is already
    /// running") and no tab is left to stop it from. Call it straight after starting the process, before it can start
    /// any of its own.
    /// </summary>
    public static void EndWithApp(Process process)
    {
        if (AppJob.Value is not { } job) return;
        try { AssignProcessToJobObject(job, process.SafeHandle); }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException) { }
    }

    /// <summary>Whether <paramref name="pid"/> was started by a process given to <see cref="EndWithApp"/>, or is one.</summary>
    public static bool IsStartedByApp(int pid)
    {
        if (!AppJob.IsValueCreated || AppJob.Value is not { } job) return false;
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        return !process.IsInvalid && IsProcessInJob(process, job, out var inJob) && inJob;
    }

    /// <summary>
    /// Whether <paramref name="pid"/> is <paramref name="root"/> or a process under it, while every process between them
    /// is still running.
    /// </summary>
    public static bool Contains(Process root, int pid)
    {
        int rootId;
        try
        {
            if (root.HasExited) return false;
            rootId = root.Id;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }

        var found = false;
        WalkUp(pid, (id, _) => !(found = id == rootId));
        return found;
    }

    /// <summary>
    /// The command lines of <paramref name="pid"/> and of the processes that started it that are still running, nearest
    /// first. They name the folder a process runs from more often than not
    /// (node C:\...\app\node_modules\next\dist\server\lib\start-server.js). Empty once it has ended.
    /// </summary>
    public static List<string> CommandLines(int pid)
    {
        var lines = new List<string>();
        WalkUp(pid, (id, process) =>
        {
            if (id == Environment.ProcessId) return false; // the app itself, which runs no project
            if (CommandLineOf(process) is { Length: > 0 } line) lines.Add(line);
            return lines.Count < 8;
        });
        return lines;
    }

    // Calls visit with pid and each process above it, nearest first, until visit says stop or the line breaks: a parent
    // that has ended, or whose id now belongs to a process started after the child, which isn't its parent.
    private static void WalkUp(int pid, Func<int, SafeProcessHandle, bool> visit)
    {
        var parents = new Dictionary<int, int>();
        foreach (var (id, parentId) in Snapshot()) parents[id] = parentId;

        var childStarted = long.MaxValue;
        for (var (id, depth) = (pid, 0); id > 4 && depth < 32; depth++)
        {
            using var process = OpenProcess(ProcessQueryLimitedInformation, false, id);
            if (process.IsInvalid || !GetProcessTimes(process, out var started, out _, out _, out _) || started > childStarted)
                return;
            if (!visit(id, process) || !parents.TryGetValue(id, out var parentId) || parentId == id) return;
            childStarted = started;
            id = parentId;
        }
    }

    // The command line the process was started with; null when Windows won't say.
    private static string? CommandLineOf(SafeProcessHandle process)
    {
        const int ProcessCommandLineInformation = 60;
        NtQueryInformationProcess(process, ProcessCommandLineInformation, IntPtr.Zero, 0, out var length);
        if (length <= 0 || length > 1 << 20) return null;

        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (NtQueryInformationProcess(process, ProcessCommandLineInformation, buffer, length, out _) != 0) return null;
            // A UNICODE_STRING: its length in bytes, its capacity, then (aligned) a pointer to the text, which follows it.
            var bytes = (ushort)Marshal.ReadInt16(buffer);
            var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
            return text == IntPtr.Zero ? null : Marshal.PtrToStringUni(text, bytes / 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static SafeFileHandle? CreateAppJob()
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            job.Dispose();
            return null;
        }

        var limits = new JobObjectExtendedLimits { Basic = { LimitFlags = JobObjectLimitKillOnJobClose } };
        if (SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf<JobObjectExtendedLimits>()))
            return job;

        job.Dispose();
        return null;
    }

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

    // JOBOBJECT_EXTENDED_LIMIT_INFORMATION, with its JOBOBJECT_BASIC_LIMIT_INFORMATION and IO_COUNTERS.
    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimits
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimits
    {
        public JobObjectBasicLimits Basic;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref JobObjectExtendedLimits info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(SafeProcessHandle process, SafeFileHandle job, out bool result);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeProcessHandle process, int infoClass, IntPtr info, int length, out int returnLength);

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
