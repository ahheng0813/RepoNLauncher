using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace RepoNLauncher.Services;

/// <summary>A place found by name: what it's called, the rest of its address, where it is, and the zoom that shows it.</summary>
public record Place(string Name, string Address, double Latitude, double Longitude, int Zoom)
{
    public string FullName => Address.Length > 0 ? $"{Name}, {Address}" : Name;
}

/// <summary>
/// Finds places by name with OpenStreetMap's Nominatim. Its usage policy
/// (https://operations.osmfoundation.org/policies/nominatim/) allows searches a person asks for, at most one a second
/// from the whole app, and no search-as-you-type; the same search isn't sent twice in a run. When the service turns the
/// app away, searching stops until the dashboard restarts.
/// </summary>
public static class PlaceSearch
{
    private const string SearchUrl = "https://nominatim.openstreetmap.org/search";
    private const int MaxResults = 5;
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1.1);

    // The Location map's size, near enough, for the zoom that shows a place whole.
    private const double MapWidth = 350, MapHeight = 230;

    private static readonly SemaphoreSlim OneAtATime = new(1);
    private static readonly Dictionary<string, IReadOnlyList<Place>> Results = new();
    private static DateTime _lastSearch = DateTime.MinValue;
    private static string? _turnedAway;

    /// <summary>Up to five matches, best first; empty when nothing matches.</summary>
    /// <exception cref="InvalidOperationException">The search couldn't be made; the message says why.</exception>
    public static async Task<IReadOnlyList<Place>> SearchAsync(string text, CancellationToken cancellation = default)
    {
        var query = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var language = CultureInfo.CurrentUICulture.Name;
        var cacheKey = $"{language}\n{query.ToLowerInvariant()}";

        await OneAtATime.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (Results.TryGetValue(cacheKey, out var known)) return known;
            if (_turnedAway != null) throw new InvalidOperationException(_turnedAway);

            var wait = _lastSearch + MinInterval - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellation).ConfigureAwait(false);
            _lastSearch = DateTime.UtcNow;

            var url = $"{SearchUrl}?format=jsonv2&limit={MaxResults}&q={Uri.EscapeDataString(query)}"
                      + (language.Length > 0 ? $"&accept-language={Uri.EscapeDataString(language)}" : "");
            string json;
            try
            {
                using var response = await OpenStreetMap.Http.GetAsync(url, cancellation).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.Forbidden or (HttpStatusCode)509)
                {
                    _turnedAway = "OpenStreetMap's place search isn't taking searches from this app now. Pin the place on the map, or type its coordinates.";
                    throw new InvalidOperationException(_turnedAway);
                }
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    throw new InvalidOperationException("OpenStreetMap's place search has had too many searches. Try again in a minute.");
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"OpenStreetMap's place search answered {(int)response.StatusCode} {response.ReasonPhrase}. Try again in a moment.");
                json = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                throw new InvalidOperationException("Couldn't reach OpenStreetMap's place search. Check this PC's internet connection.");
            }
            catch (TaskCanceledException) when (!cancellation.IsCancellationRequested)
            {
                throw new InvalidOperationException("OpenStreetMap's place search took too long to answer. Try again in a moment.");
            }

            var places = Parse(json);
            Results[cacheKey] = places;
            return places;
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    private static IReadOnlyList<Place> Parse(string json)
    {
        var places = new List<Place>();
        try
        {
            using var document = JsonDocument.Parse(json);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (!TryNumber(item, "lat", out var latitude) || !TryNumber(item, "lon", out var longitude)) continue;

                var fullName = item.TryGetProperty("display_name", out var d) ? d.GetString() ?? "" : "";
                var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (name.Length == 0) name = fullName.Split(',')[0].Trim();
                var address = fullName.StartsWith(name + ", ", StringComparison.Ordinal) ? fullName[(name.Length + 2)..]
                    : fullName == name ? "" : fullName;

                var zoom = 16;
                if (item.TryGetProperty("boundingbox", out var box) && box.GetArrayLength() == 4
                    && double.TryParse(box[0].GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var south)
                    && double.TryParse(box[1].GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var north)
                    && double.TryParse(box[2].GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var west)
                    && double.TryParse(box[3].GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var east))
                    zoom = WebMercator.ZoomToShow(south, west, north, east, MapWidth, MapHeight);

                places.Add(new Place(name, address, latitude, longitude, zoom));
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException("OpenStreetMap's place search sent an answer this app can't read.");
        }
        return places;
    }

    private static bool TryNumber(JsonElement item, string name, out double value)
    {
        value = 0;
        return item.TryGetProperty(name, out var property)
               && double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
