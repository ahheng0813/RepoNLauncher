using System.Globalization;
using System.IO;
using RepoNLauncher.Models;

namespace RepoNLauncher.Services;

public interface IGitService
{
    bool IsGitRepo(string path);
    bool HasMergeConflict(string path);
    Task<GitPullResult> PullAsync(string path, CancellationToken ct = default);
    Task<GitPullResult> MergeAsync(string path, string branch, CancellationToken ct = default);
    Task<string> GetCurrentBranchAsync(string path, CancellationToken ct = default);
    Task<GitStatusSummary> GetStatusAsync(string path, CancellationToken ct = default);
    Task<List<CommitInfo>> GetRecentCommitsAsync(string path, int count = 30, CancellationToken ct = default);

    Task<List<FileChangeInfo>> GetChangedFilesAsync(string path, CancellationToken ct = default);
    Task<string> GetFileDiffAsync(string path, FileChangeInfo file, CancellationToken ct = default);
    Task<GitPullResult> DiscardFileAsync(string path, FileChangeInfo file, CancellationToken ct = default);
    Task<GitPullResult> DiscardAllAsync(string path, CancellationToken ct = default);

    Task<GitPullResult> StashAsync(string path, string? message, CancellationToken ct = default);
    Task<List<StashInfo>> GetStashListAsync(string path, CancellationToken ct = default);
    Task<GitPullResult> StashApplyAsync(string path, string reference, CancellationToken ct = default);
    Task<GitPullResult> StashDropAsync(string path, string reference, CancellationToken ct = default);

    Task<GitPullResult> ResolveConflictAsync(string path, string filePath, ConflictResolution resolution, CancellationToken ct = default);
    Task<GitPullResult> MarkResolvedAsync(string path, string filePath, CancellationToken ct = default);
    Task<GitPullResult> AbortMergeAsync(string path, CancellationToken ct = default);
    Task<GitPullResult> CommitMergeAsync(string path, CancellationToken ct = default);
    Task<GitPullResult> CommitAllAsync(string path, string message, CancellationToken ct = default);
}

public class GitService : IGitService
{
    private const string FieldSep = "\u001f";
    private const string RecordSep = "\u001e";
    private readonly IProcessRunner _runner;

    public GitService(IProcessRunner runner) => _runner = runner;

    public bool IsGitRepo(string path) => Directory.Exists(Path.Combine(path, ".git"));

    public bool HasMergeConflict(string path) => File.Exists(Path.Combine(path, ".git", "MERGE_HEAD"));

    private static GitPullResult BuildResult(CaptureResult result)
    {
        var output = (result.StdOut + result.StdErr).Trim();
        return new GitPullResult
        {
            Success = result.ExitCode == 0,
            Output = output,
            AlreadyUpToDate = output.Contains("Already up to date", StringComparison.OrdinalIgnoreCase),
        };
    }

    public async Task<GitPullResult> PullAsync(string path, CancellationToken ct = default) =>
        BuildResult(await _runner.RunCaptureAsync(path, "git pull --ff-only", ct));

    public async Task<GitPullResult> MergeAsync(string path, string branch, CancellationToken ct = default) =>
        BuildResult(await _runner.RunCaptureAsync(path, $"git merge --no-edit {branch}", ct));

    public async Task<string> GetCurrentBranchAsync(string path, CancellationToken ct = default)
    {
        var result = await _runner.RunCaptureAsync(path, "git rev-parse --abbrev-ref HEAD", ct);
        return result.StdOut.Trim();
    }

    public async Task<GitStatusSummary> GetStatusAsync(string path, CancellationToken ct = default)
    {
        var summary = new GitStatusSummary();

        summary.Branch = await GetCurrentBranchAsync(path, ct);

        var porcelain = await _runner.RunCaptureAsync(path, "git status --porcelain", ct);
        summary.IsDirty = !string.IsNullOrWhiteSpace(porcelain.StdOut);

        var aheadBehind = await _runner.RunCaptureAsync(path, "git rev-list --left-right --count HEAD...@{u}", ct);
        if (aheadBehind.ExitCode == 0)
        {
            var parts = aheadBehind.StdOut.Trim().Split('\t', ' ');
            if (parts.Length == 2 && int.TryParse(parts[0], out var ahead) && int.TryParse(parts[1], out var behind))
            {
                summary.Ahead = ahead;
                summary.Behind = behind;
            }
        }

        return summary;
    }

    public async Task<List<CommitInfo>> GetRecentCommitsAsync(string path, int count = 30, CancellationToken ct = default)
    {
        var format = $"%H{FieldSep}%an{FieldSep}%aI{FieldSep}%s{RecordSep}";
        var result = await _runner.RunCaptureAsync(path, $"git log -n {count} --pretty=format:{format}", ct);

        var commits = new List<CommitInfo>();
        if (result.ExitCode != 0) return commits;

        var records = result.StdOut.Split(RecordSep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var record in records)
        {
            var fields = record.Split(FieldSep);
            if (fields.Length < 4) continue;

            commits.Add(new CommitInfo
            {
                Hash = fields[0],
                Author = fields[1],
                Date = DateTimeOffset.TryParse(fields[2], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    ? date
                    : DateTimeOffset.MinValue,
                Message = fields[3],
            });
        }

        return commits;
    }

    public async Task<List<FileChangeInfo>> GetChangedFilesAsync(string path, CancellationToken ct = default)
    {
        var result = await _runner.RunCaptureAsync(path, "git status --porcelain=v1 -z", ct);
        var changes = new List<FileChangeInfo>();
        if (string.IsNullOrEmpty(result.StdOut)) return changes;

        var tokens = result.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length; i++)
        {
            var entry = tokens[i];
            if (entry.Length < 4) continue;

            var x = entry[0];
            var y = entry[1];
            var filePath = entry[3..];

            string? originalPath = null;
            if (x is 'R' or 'C' || y is 'R' or 'C')
            {
                if (i + 1 < tokens.Length) originalPath = tokens[++i];
            }

            changes.Add(new FileChangeInfo
            {
                Path = filePath,
                OriginalPath = originalPath,
                Type = ClassifyChange(x, y),
                IsStaged = x != ' ' && x != '?',
            });
        }

        return changes;
    }

    private static FileChangeType ClassifyChange(char x, char y)
    {
        if (x == '?' && y == '?') return FileChangeType.Untracked;
        if (x == 'U' || y == 'U' || (x == 'A' && y == 'A') || (x == 'D' && y == 'D')) return FileChangeType.Conflicted;
        if (x == 'R' || y == 'R') return FileChangeType.Renamed;
        if (x == 'C' || y == 'C') return FileChangeType.Copied;
        if (x == 'D' || y == 'D') return FileChangeType.Deleted;
        if (x == 'A' || y == 'A') return FileChangeType.Added;
        return FileChangeType.Modified;
    }

    public async Task<string> GetFileDiffAsync(string path, FileChangeInfo file, CancellationToken ct = default)
    {
        if (file.Type == FileChangeType.Untracked)
        {
            var fullPath = Path.Combine(path, file.Path);
            if (!File.Exists(fullPath)) return "(file not found)";
            try
            {
                var text = await File.ReadAllTextAsync(fullPath, ct);
                var lines = text.Replace("\r\n", "\n").Split('\n');
                return string.Join('\n', lines.Select(l => "+" + l));
            }
            catch (IOException)
            {
                return "(unable to read file — likely binary)";
            }
        }

        // --no-ext-diff bypasses any diff.external tool (e.g. Diffchecker) configured globally,
        // which would otherwise shell out to a GUI tool instead of returning text here.
        var result = await _runner.RunCaptureAsync(path, $"git diff --no-ext-diff HEAD -- \"{file.Path}\"", ct);
        var diff = result.StdOut.Trim();
        return string.IsNullOrEmpty(diff) ? "(no textual changes — possibly a binary file)" : diff;
    }

    public async Task<GitPullResult> DiscardFileAsync(string path, FileChangeInfo file, CancellationToken ct = default)
    {
        var command = file.Type == FileChangeType.Untracked
            ? $"git clean -f -- \"{file.Path}\""
            : $"git checkout -- \"{file.Path}\"";
        return BuildResult(await _runner.RunCaptureAsync(path, command, ct));
    }

    public async Task<GitPullResult> DiscardAllAsync(string path, CancellationToken ct = default)
    {
        var reset = await _runner.RunCaptureAsync(path, "git reset --hard HEAD", ct);
        var clean = await _runner.RunCaptureAsync(path, "git clean -fd", ct);
        var output = $"{reset.StdOut}{reset.StdErr}\n{clean.StdOut}{clean.StdErr}".Trim();
        return new GitPullResult { Success = reset.ExitCode == 0 && clean.ExitCode == 0, Output = output };
    }

    public async Task<GitPullResult> StashAsync(string path, string? message, CancellationToken ct = default)
    {
        var command = string.IsNullOrWhiteSpace(message)
            ? "git stash push -u"
            : $"git stash push -u -m \"{message}\"";
        return BuildResult(await _runner.RunCaptureAsync(path, command, ct));
    }

    public async Task<List<StashInfo>> GetStashListAsync(string path, CancellationToken ct = default)
    {
        var format = $"%gd{FieldSep}%aI{FieldSep}%s{RecordSep}";
        var result = await _runner.RunCaptureAsync(path, $"git stash list --pretty=format:{format}", ct);

        var stashes = new List<StashInfo>();
        if (string.IsNullOrWhiteSpace(result.StdOut)) return stashes;

        var records = result.StdOut.Split(RecordSep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var record in records)
        {
            var fields = record.Split(FieldSep);
            if (fields.Length < 3) continue;

            stashes.Add(new StashInfo
            {
                Reference = fields[0],
                Date = DateTimeOffset.TryParse(fields[1], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    ? date
                    : DateTimeOffset.MinValue,
                Message = fields[2],
            });
        }

        return stashes;
    }

    public async Task<GitPullResult> StashApplyAsync(string path, string reference, CancellationToken ct = default) =>
        BuildResult(await _runner.RunCaptureAsync(path, $"git stash apply {reference}", ct));

    public async Task<GitPullResult> StashDropAsync(string path, string reference, CancellationToken ct = default) =>
        BuildResult(await _runner.RunCaptureAsync(path, $"git stash drop {reference}", ct));

    public async Task<GitPullResult> ResolveConflictAsync(string path, string filePath, ConflictResolution resolution, CancellationToken ct = default)
    {
        var flag = resolution == ConflictResolution.Ours ? "--ours" : "--theirs";
        var checkoutResult = await _runner.RunCaptureAsync(path, $"git checkout {flag} -- \"{filePath}\"", ct);
        if (checkoutResult.ExitCode != 0) return BuildResult(checkoutResult);

        return BuildResult(await _runner.RunCaptureAsync(path, $"git add -- \"{filePath}\"", ct));
    }

    public async Task<GitPullResult> MarkResolvedAsync(string path, string filePath, CancellationToken ct = default) =>
        BuildResult(await _runner.RunCaptureAsync(path, $"git add -- \"{filePath}\"", ct));

    public async Task<GitPullResult> AbortMergeAsync(string path, CancellationToken ct = default) =>
        BuildResult(await _runner.RunCaptureAsync(path, "git merge --abort", ct));

    public async Task<GitPullResult> CommitMergeAsync(string path, CancellationToken ct = default) =>
        BuildResult(await _runner.RunCaptureAsync(path, "git commit --no-edit", ct));

    public async Task<GitPullResult> CommitAllAsync(string path, string message, CancellationToken ct = default)
    {
        var add = await _runner.RunCaptureAsync(path, "git add -A", ct);
        if (add.ExitCode != 0) return BuildResult(add);

        // -F reads the message from a file, so quotes, & or newlines in it can't break the cmd.exe command line.
        var messageFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(messageFile, message, ct);
            return BuildResult(await _runner.RunCaptureAsync(path, $"git commit -F \"{messageFile}\"", ct));
        }
        finally
        {
            File.Delete(messageFile);
        }
    }
}
