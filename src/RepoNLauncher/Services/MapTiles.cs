using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows.Media.Imaging;

namespace RepoNLauncher.Services;

/// <summary>A map tile: its zoom level, then its column and row.</summary>
public readonly record struct TileKey(int Zoom, int X, int Y);

/// <summary>
/// OpenStreetMap's map pictures for the Location map, kept in memory and on disk as its tile usage policy
/// (https://operations.osmfoundation.org/policies/tiles/) asks: only tiles being shown are fetched, and a saved tile is
/// used until its cache headers say it's out of date (a week when they say nothing), then checked with a conditional
/// request. Their stale-while-revalidate lets an out-of-date tile be shown while it's checked in the background.
/// </summary>
public static class MapTiles
{
    private const string TileUrl = "https://tile.openstreetmap.org/{0}/{1}/{2}.png";
    private const int MemoryTiles = 300;
    // Once a run, the tiles on disk are cut back to three quarters of this, oldest first, when there are more.
    private const long DiskBytes = 200L * 1024 * 1024;
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(7);
    // A tile that couldn't be had isn't asked for again for this long, so a map shown offline doesn't keep trying.
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(20);

    // Beside the settings folder, but in Local: a cache has no business roaming.
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Path.GetFileName(DashboardConfigService.AppDataDir), "MapTiles");

    // As few connections to the server as a browser would use.
    private static readonly SemaphoreSlim Downloads = new(4);

    private static readonly object Lock = new();
    private static readonly Dictionary<TileKey, LinkedListNode<(TileKey Key, BitmapSource Image)>> Memory = new();
    private static readonly LinkedList<(TileKey Key, BitmapSource Image)> RecentlyUsed = new();
    private static readonly Dictionary<TileKey, DateTime> Failed = new();
    private static readonly HashSet<TileKey> Checking = new();
    private static int _trimmed;

    /// <summary>The last download failed for want of a connection, and none has worked since.</summary>
    public static bool IsOffline { get; private set; }

    /// <summary>The tile, if it's in memory.</summary>
    public static BitmapSource? Cached(TileKey key)
    {
        lock (Lock)
        {
            if (!Memory.TryGetValue(key, out var node)) return null;
            RecentlyUsed.Remove(node);
            RecentlyUsed.AddFirst(node);
            return node.Value.Image;
        }
    }

    /// <summary>Gets the tile from disk or OpenStreetMap and keeps it in memory; null when it can't be had now.</summary>
    public static async Task<BitmapSource?> LoadAsync(TileKey key, CancellationToken cancellation)
    {
        lock (Lock)
        {
            if (Failed.TryGetValue(key, out var until) && DateTime.UtcNow < until) return null;
        }

        var path = Path.Combine(CacheDir, key.Zoom.ToString(CultureInfo.InvariantCulture), key.X.ToString(CultureInfo.InvariantCulture),
            key.Y.ToString(CultureInfo.InvariantCulture) + ".png");
        var image = await Task.Run(() => FetchAsync(key, path, cancellation), cancellation).ConfigureAwait(false);

        lock (Lock)
        {
            if (image == null)
            {
                Failed[key] = DateTime.UtcNow + RetryAfter;
                return null;
            }

            Failed.Remove(key);
            if (Memory.TryGetValue(key, out var old)) RecentlyUsed.Remove(old);
            Memory[key] = RecentlyUsed.AddFirst((key, image));
            if (Memory.Count > MemoryTiles)
            {
                Memory.Remove(RecentlyUsed.Last!.Value.Key);
                RecentlyUsed.RemoveLast();
            }
        }
        return image;
    }

    private static async Task<BitmapSource?> FetchAsync(TileKey key, string path, CancellationToken cancellation)
    {
        // A saved tile that can't be read is fetched afresh.
        var saved = SavedTile.Read(path);
        var savedImage = saved != null ? Decode(await ReadAsync(path)) : null;
        if (savedImage == null) saved = null;

        var now = DateTimeOffset.UtcNow;
        if (saved != null && now < saved.Expires) return savedImage;
        if (saved != null && now < saved.StaleUntil)
        {
            CheckInBackground(key, path, saved);
            return savedImage;
        }
        // An out-of-date tile beats none when the server can't be reached.
        return await DownloadAsync(key, path, saved, cancellation).ConfigureAwait(false) ?? savedImage;
    }

    // Shows the saved tile now and asks the server whether it changed; a newer one replaces it in memory.
    private static void CheckInBackground(TileKey key, string path, SavedTile saved)
    {
        lock (Lock)
        {
            if (!Checking.Add(key)) return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                if (await DownloadAsync(key, path, saved, CancellationToken.None).ConfigureAwait(false) is not { } newer) return;
                lock (Lock)
                {
                    if (Memory.TryGetValue(key, out var node)) node.Value = (key, newer);
                }
            }
            finally
            {
                lock (Lock) Checking.Remove(key);
            }
        });
    }

    /// <summary>
    /// The tile from the server, and saves it. With one saved already, asks only whether it changed: null when it hasn't,
    /// or when it can't be had now.
    /// </summary>
    private static async Task<BitmapSource?> DownloadAsync(TileKey key, string path, SavedTile? saved, CancellationToken cancellation)
    {
        await Downloads.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                string.Format(CultureInfo.InvariantCulture, TileUrl, key.Zoom, key.X, key.Y));
            if (saved?.ETag is { } etag && EntityTagHeaderValue.TryParse(etag, out var tag)) request.Headers.IfNoneMatch.Add(tag);
            if (saved?.LastModified is { } modified) request.Headers.IfModifiedSince = modified;

            using var response = await OpenStreetMap.Http.SendAsync(request, cancellation).ConfigureAwait(false);
            IsOffline = false;
            if (response.StatusCode == HttpStatusCode.NotModified && saved != null)
            {
                var (expires, staleUntil) = Lifetime(response);
                Save(path, null, saved with { Expires = expires, StaleUntil = staleUntil });
                return null;
            }
            if (!response.IsSuccessStatusCode) return null;

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellation).ConfigureAwait(false);
            if (Decode(bytes) is not { } image) return null;
            var lifetime = Lifetime(response);
            Save(path, bytes, new SavedTile(lifetime.Expires, lifetime.StaleUntil, response.Headers.ETag?.ToString(),
                response.Content.Headers.LastModified));
            return image;
        }
        catch (Exception e) when (e is HttpRequestException
                                  || (e is TaskCanceledException && !cancellation.IsCancellationRequested))
        {
            IsOffline = e is HttpRequestException; // or it took too long
            return null;
        }
        finally
        {
            Downloads.Release();
        }
    }

    // The tile (null when it's unchanged) and what the server said about it.
    private static void Save(string path, byte[]? bytes, SavedTile info)
    {
        try
        {
            if (bytes != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bytes);
                TrimOnce();
            }
            else
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow); // still in use, so trimmed last
            }
            info.Write(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // another map saving the same tile, say: it's fetched again next time
        }
    }

    // How long a tile may be shown as it is, and how much longer while it's checked, from its cache headers.
    private static (DateTimeOffset Expires, DateTimeOffset StaleUntil) Lifetime(HttpResponseMessage response)
    {
        var now = DateTimeOffset.UtcNow;
        var cache = response.Headers.CacheControl;
        if (cache is { NoStore: true } or { NoCache: true }) return (now, now);

        var expires = cache?.MaxAge is { } maxAge
            ? now + maxAge - (response.Headers.Age ?? TimeSpan.Zero)
            : response.Content.Headers.Expires ?? now + DefaultLifetime;
        var stale = cache?.Extensions.FirstOrDefault(e => e.Name.Equals("stale-while-revalidate", StringComparison.OrdinalIgnoreCase));
        var staleFor = int.TryParse(stale?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.Zero;
        return (expires, expires + staleFor);
    }

    private static async Task<byte[]?> ReadAsync(string path)
    {
        try
        {
            return await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static BitmapSource? Decode(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 }) return null;
        try
        {
            using var stream = new MemoryStream(bytes);
            var image = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            image.Freeze(); // decoded off the UI thread, drawn on it
            return image;
        }
        catch (Exception e) when (e is NotSupportedException or FileFormatException or ArgumentException)
        {
            return null;
        }
    }

    private static void TrimOnce()
    {
        if (Interlocked.Exchange(ref _trimmed, 1) != 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                var tiles = new DirectoryInfo(CacheDir).EnumerateFiles("*.png", SearchOption.AllDirectories).ToList();
                var total = tiles.Sum(t => t.Length);
                if (total <= DiskBytes) return;
                foreach (var tile in tiles.OrderBy(t => t.LastWriteTimeUtc))
                {
                    if (total <= DiskBytes / 4 * 3) break;
                    total -= tile.Length;
                    tile.Delete();
                    File.Delete(SavedTile.InfoPath(tile.FullName));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // in use, or gone already: the next run trims it
            }
        });
    }

    /// <summary>What the server said about a tile saved on disk, in a text file beside it.</summary>
    private sealed record SavedTile(DateTimeOffset Expires, DateTimeOffset StaleUntil, string? ETag, DateTimeOffset? LastModified)
    {
        public static string InfoPath(string path) => path + ".info";

        /// <summary>Null when there's no saved tile; one without its info is out of date.</summary>
        public static SavedTile? Read(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                var lines = File.ReadAllLines(InfoPath(path));
                return new SavedTile(
                    DateTimeOffset.Parse(lines[0], CultureInfo.InvariantCulture),
                    DateTimeOffset.Parse(lines[1], CultureInfo.InvariantCulture),
                    lines[2].Length > 0 ? lines[2] : null,
                    lines[3].Length > 0 ? DateTimeOffset.Parse(lines[3], CultureInfo.InvariantCulture) : null);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or IndexOutOfRangeException)
            {
                return new SavedTile(DateTimeOffset.MinValue, DateTimeOffset.MinValue, null, null);
            }
        }

        public void Write(string path) => File.WriteAllLines(InfoPath(path),
        [
            Expires.ToString("o", CultureInfo.InvariantCulture),
            StaleUntil.ToString("o", CultureInfo.InvariantCulture),
            ETag ?? "",
            LastModified?.ToString("o", CultureInfo.InvariantCulture) ?? "",
        ]);
    }
}
