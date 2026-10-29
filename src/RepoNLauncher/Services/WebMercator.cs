using RepoNLauncher.Models;

namespace RepoNLauncher.Services;

/// <summary>
/// The square world of web maps: a place's position as a share (0 to 1) of the world's width from longitude -180, and of
/// its height from the top. At zoom level z the world is 256 × 2^z pixels, in 2^z × 2^z tiles.
/// </summary>
public static class WebMercator
{
    public const int TileSize = 256;

    /// <summary>The map is square only this far north and south.</summary>
    public const double MaxLatitude = 85.05112878;

    public static (double X, double Y) ToWorld(GeoPoint place)
    {
        var latitude = Math.Clamp(place.Latitude, -MaxLatitude, MaxLatitude) * Math.PI / 180;
        return ((place.Longitude + 180) / 360,
                (1 - Math.Log(Math.Tan(latitude) + 1 / Math.Cos(latitude)) / Math.PI) / 2);
    }

    /// <summary>The place at a world position; one off the side wraps around to the other.</summary>
    public static GeoPoint FromWorld(double x, double y)
    {
        x -= Math.Floor(x);
        var latitude = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * Math.Clamp(y, 0, 1)))) * 180 / Math.PI;
        return new GeoPoint(latitude, x * 360 - 180);
    }

    /// <summary>The closest zoom level that shows all of an area on a map of this size, in pixels.</summary>
    public static int ZoomToShow(double south, double west, double north, double east, double width, double height)
    {
        var (left, bottom) = ToWorld(new GeoPoint(south, west));
        var (right, top) = ToWorld(new GeoPoint(north, east));
        var across = Math.Max(Math.Abs(right - left), 1e-9);
        var down = Math.Max(Math.Abs(bottom - top), 1e-9);
        var zoom = Math.Log2(Math.Min(width / across, height / down) / TileSize);
        return (int)Math.Clamp(Math.Floor(zoom), 2, 17);
    }
}
