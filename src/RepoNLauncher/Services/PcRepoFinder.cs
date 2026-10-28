using System.Collections.Concurrent;
using System.IO;

namespace RepoNLauncher.Services;

public record PcScanProgress(int FoldersChecked, int ReposFound);

/// <summary>
/// Finds every git repo on the PC's local drives. Skips hidden, system and dot-folders, installed software, app data,
/// and dependency/build folders, and doesn't look inside a repo once found.
/// </summary>
public static class PcRepoFinder
{
    // Folders that hold installed software, app data, caches or downloaded dependencies rather than the user's own
    // repos. AppData and ProgramData are usually hidden, but not on every PC. Dot-folders (.gradle, .vscode, .venv…)
    // are tools' caches and settings, so any folder starting with "." is skipped too.
    private static readonly HashSet<string> SkipDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "AppData", "$Recycle.Bin", "System Volume Information",
        "Recovery", "PerfLogs", "node_modules", "bower_components", "bin", "obj", "build", "Pods", "venv", "site-packages", "__pycache__",
    };

    private static readonly EnumerationOptions ListEverything = new() { IgnoreInaccessible = true, AttributesToSkip = 0 };

    private const int ReportEvery = 400;

    public static List<string> FindAll(IProgress<PcScanProgress>? progress, CancellationToken ct)
    {
        var roots = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => d.RootDirectory)
            .ToList();

        var pending = new ConcurrentStack<DirectoryInfo>(roots);
        var repos = new ConcurrentBag<string>();
        var outstanding = roots.Count; // folders queued or being looked at; the walk is done when none are left
        var checkedCount = 0;

        // A few workers share one stack, so a folder with a huge subtree (the user profile) doesn't leave the rest idle.
        void Work()
        {
            while (Volatile.Read(ref outstanding) > 0)
            {
                ct.ThrowIfCancellationRequested();
                if (!pending.TryPop(out var dir))
                {
                    Thread.Sleep(1);
                    continue;
                }

                var subDirs = Inspect(dir, repos);
                if (subDirs.Count > 0)
                {
                    Interlocked.Add(ref outstanding, subDirs.Count);
                    pending.PushRange(subDirs.ToArray());
                }
                Interlocked.Decrement(ref outstanding);

                if (Interlocked.Increment(ref checkedCount) % ReportEvery == 0)
                    progress?.Report(new PcScanProgress(checkedCount, repos.Count));
            }
        }

        var workers = Enumerable.Range(0, Math.Clamp(Environment.ProcessorCount, 2, 6)).Select(_ => Task.Run(Work, ct)).ToArray();
        try { Task.WaitAll(workers); }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException)) { }
        ct.ThrowIfCancellationRequested();

        progress?.Report(new PcScanProgress(checkedCount, repos.Count));
        return repos.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Records the folder if it's a repo, otherwise returns the subfolders worth walking into.
    private static List<DirectoryInfo> Inspect(DirectoryInfo dir, ConcurrentBag<string> repos)
    {
        var subDirs = new List<DirectoryInfo>();
        try
        {
            foreach (var entry in dir.EnumerateFileSystemInfos("*", ListEverything))
            {
                // .git is a folder in a normal clone and a file in a worktree or submodule.
                if (entry.Name.Equals(".git", StringComparison.OrdinalIgnoreCase))
                {
                    repos.Add(dir.FullName.TrimEnd(Path.DirectorySeparatorChar));
                    return [];
                }

                if (entry is DirectoryInfo sub && ShouldWalk(sub)) subDirs.Add(sub);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return subDirs;
    }

    private static bool ShouldWalk(DirectoryInfo dir)
    {
        if ((dir.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) return false;
        if (dir.Name.StartsWith('.') || SkipDirNames.Contains(dir.Name)) return false;
        // Junctions and symlinks lead to folders found elsewhere anyway, or loop. Other reparse points (OneDrive
        // folders) have no link target and are walked as usual.
        if ((dir.Attributes & FileAttributes.ReparsePoint) == 0) return true;
        try { return dir.LinkTarget == null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
