using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RepoNLauncher.Services;

/// <summary>A picture made small enough to show, upright, with the width and height of the whole picture.</summary>
public record DecodedPicture(BitmapSource Image, int Width, int Height);

/// <summary>
/// Pictures from a phone, made small enough to show and turned the way the camera was held. Phones save photos sideways
/// with an EXIF note of which way is up, which Windows' decoders ignore.
/// </summary>
public static class PhonePictures
{
    /// <summary>
    /// The picture, no wider or taller than <paramref name="size"/>, turned to <paramref name="orientation"/> (an EXIF
    /// orientation), or by the one in its own EXIF when that's null; null when Windows can't read it.
    /// </summary>
    public static DecodedPicture? Decode(byte[] data, int size, int? orientation = null)
    {
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            // The header alone, for how big the whole picture is.
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
            int width = frame.PixelWidth, height = frame.PixelHeight;

            stream.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            // One side only, so the other keeps the picture's shape.
            if (width >= height) image.DecodePixelWidth = Math.Min(size, width);
            else image.DecodePixelHeight = Math.Min(size, height);
            image.StreamSource = stream;
            image.EndInit();

            var turn = orientation ?? ReadExif(data).Orientation;
            BitmapSource upright = Upright(turn) is { } transform ? new TransformedBitmap(image, transform) : image;
            upright.Freeze();
            // 5 to 8 are a quarter turn, which swaps the sides.
            return turn >= 5 ? new DecodedPicture(upright, height, width) : new DecodedPicture(upright, width, height);
        }
        catch (Exception e) when (e is IOException or FormatException or NotSupportedException or InvalidOperationException
                                      or ArgumentException or OverflowException or ExternalException)
        {
            return null;
        }
    }

    /// <summary>
    /// A small upright copy of the picture to keep, as a JPEG (or a PNG when the picture isn't a JPEG, which may have
    /// see-through parts); null when Windows can't read it.
    /// </summary>
    public static byte[]? MakeThumbnail(byte[] data, int size, int? orientation = null)
    {
        if (Decode(data, size, orientation) is not { } picture) return null;
        BitmapEncoder encoder = IsJpeg(data) ? new JpegBitmapEncoder { QualityLevel = 85 } : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(picture.Image));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    /// <summary>
    /// The small copy most cameras keep in a JPEG's EXIF, from the file's first bytes, and which way up the photo is
    /// (which applies to the copy too); null when it has none.
    /// </summary>
    public static (byte[] Jpeg, int Orientation)? ExifThumbnail(byte[] start)
    {
        var exif = ReadExif(start);
        return exif.Thumbnail is { } jpeg ? (jpeg, exif.Orientation) : null;
    }

    public static bool IsJpeg(byte[] data) => data.Length > 2 && data[0] == 0xFF && data[1] == 0xD8;

    // What turns a picture with this EXIF orientation upright: 1 is upright already, 6 and 8 are a quarter turn either
    // way, 3 a half turn, and the rest mirrored versions of those.
    private static Transform? Upright(int orientation) => orientation switch
    {
        2 => new ScaleTransform(-1, 1),
        3 => new RotateTransform(180),
        4 => new ScaleTransform(1, -1),
        5 => new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(270) } },
        6 => new RotateTransform(90),
        7 => new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(90) } },
        8 => new RotateTransform(270),
        _ => null,
    };

    // A JPEG is a run of segments, each a marker and its length; the EXIF one is a TIFF file of its own.
    private static (int Orientation, byte[]? Thumbnail) ReadExif(ReadOnlySpan<byte> data)
    {
        if (!(data.Length > 4 && data[0] == 0xFF && data[1] == 0xD8)) return (1, null);
        var at = 2;
        while (at + 4 <= data.Length && data[at] == 0xFF)
        {
            var marker = data[at + 1];
            if (marker == 0xFF)
            {
                at++; // padding before a marker
                continue;
            }
            if (marker is 0xDA or 0xD9) break; // the picture itself, which nothing of use comes before
            var length = data[at + 2] << 8 | data[at + 3];
            if (marker == 0xE1 && length >= 8 && at + 10 <= data.Length && data.Slice(at + 4, 6).SequenceEqual("Exif\0\0"u8))
                return ReadTiff(data.Slice(at + 10, Math.Min(length - 8, data.Length - at - 10)));
            at += 2 + length;
        }
        return (1, null);
    }

    // The first of a TIFF's directories describes the photo, the second its thumbnail: a JPEG somewhere in the TIFF.
    private static (int Orientation, byte[]? Thumbnail) ReadTiff(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8 || !(tiff[0] == 'I' && tiff[1] == 'I' || tiff[0] == 'M' && tiff[1] == 'M')) return (1, null);
        var littleEndian = tiff[0] == 'I';
        int orientation = 1, thumbnailAt = 0, thumbnailLength = 0;

        var directory = Read32(tiff, 4, littleEndian);
        for (var n = 0; n < 2 && directory > 0 && directory + 2 <= tiff.Length; n++)
        {
            var entries = Read16(tiff, directory, littleEndian);
            for (var i = 0; i < entries && directory + 2 + i * 12 + 12 <= tiff.Length; i++)
            {
                var entry = directory + 2 + i * 12;
                var tag = Read16(tiff, entry, littleEndian);
                // A short value is at the start of the entry's four value bytes; a long one fills them.
                var value = Read16(tiff, entry + 2, littleEndian) == 3 ? Read16(tiff, entry + 8, littleEndian) : Read32(tiff, entry + 8, littleEndian);
                if (n == 0 && tag == 0x0112) orientation = value;
                else if (n == 1 && tag == 0x0201) thumbnailAt = value;
                else if (n == 1 && tag == 0x0202) thumbnailLength = value;
            }
            var next = directory + 2 + entries * 12;
            directory = next + 4 <= tiff.Length ? Read32(tiff, next, littleEndian) : 0;
        }

        var thumbnail = thumbnailAt > 0 && thumbnailLength > 0 && thumbnailAt <= tiff.Length - thumbnailLength
            ? tiff.Slice(thumbnailAt, thumbnailLength).ToArray()
            : null;
        return (orientation is >= 1 and <= 8 ? orientation : 1, thumbnail);
    }

    private static int Read16(ReadOnlySpan<byte> data, int at, bool littleEndian) =>
        littleEndian ? data[at] | data[at + 1] << 8 : data[at] << 8 | data[at + 1];

    // Offsets past what an int holds come out negative, which every check above takes as missing.
    private static int Read32(ReadOnlySpan<byte> data, int at, bool littleEndian) => littleEndian
        ? data[at] | data[at + 1] << 8 | data[at + 2] << 16 | data[at + 3] << 24
        : data[at] << 24 | data[at + 1] << 16 | data[at + 2] << 8 | data[at + 3];
}
