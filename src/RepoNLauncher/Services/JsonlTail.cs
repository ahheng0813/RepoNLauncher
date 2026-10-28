using System.IO;

namespace RepoNLauncher.Services;

public delegate T? JsonlLineParser<T>(ReadOnlySpan<byte> line) where T : class;

/// <summary>
/// Keeps the parsed lines of a set of JSONL files and, on each read, parses only what was appended since the last one.
/// Session transcripts run to hundreds of MB and grow while a session runs, so re-reading them whole every minute
/// would be wasteful.
/// </summary>
public sealed class JsonlTail<T> where T : class
{
    private readonly JsonlLineParser<T> _parse;
    private readonly Dictionary<string, FileState> _files = new(StringComparer.OrdinalIgnoreCase);

    public JsonlTail(JsonlLineParser<T> parse) => _parse = parse;

    /// <summary>The parsed lines of each file, in file order. Files left out of <paramref name="paths"/> are forgotten.</summary>
    public IReadOnlyDictionary<string, List<T>> Read(IEnumerable<string> paths)
    {
        var listed = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _files.Keys.Where(path => !listed.Contains(path)).ToList())
            _files.Remove(gone);

        foreach (var path in listed)
        {
            if (!_files.TryGetValue(path, out var state)) _files[path] = state = new FileState();
            try { ReadNewLines(path, state); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        return _files.ToDictionary(f => f.Key, f => f.Value.Items, StringComparer.OrdinalIgnoreCase);
    }

    private void ReadNewLines(string path, FileState state)
    {
        // Shared access: the tool is usually still writing the file.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                                          bufferSize: 1, FileOptions.SequentialScan);
        if (stream.Length < state.Offset)
        {
            // Shorter than what was read: the file was rewritten, so start over.
            state.Offset = 0;
            state.Items.Clear();
        }
        if (stream.Length == state.Offset) return;
        stream.Seek(state.Offset, SeekOrigin.Begin);

        var buffer = new byte[1 << 20];
        var carry = new MemoryStream(); // a line split across two reads
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            var chunk = buffer.AsSpan(0, read);
            int newline;
            while ((newline = chunk.IndexOf((byte)'\n')) >= 0)
            {
                if (carry.Length > 0)
                {
                    carry.Write(chunk[..newline]);
                    Add(carry.GetBuffer().AsSpan(0, (int)carry.Length), state);
                    state.Offset += carry.Length + 1;
                    carry.SetLength(0);
                }
                else
                {
                    Add(chunk[..newline], state);
                    state.Offset += newline + 1;
                }
                chunk = chunk[(newline + 1)..];
            }
            carry.Write(chunk);
        }
        // Whatever is left in carry is a line still being written; it's read again, whole, next time.
    }

    private void Add(ReadOnlySpan<byte> line, FileState state)
    {
        if (_parse(line) is { } item) state.Items.Add(item);
    }

    private sealed class FileState
    {
        public long Offset;
        public List<T> Items { get; } = new();
    }
}
