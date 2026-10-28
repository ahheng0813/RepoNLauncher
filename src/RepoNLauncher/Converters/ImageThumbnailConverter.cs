using System.Globalization;
using System.IO;
using System.Windows.Data;
using RepoNLauncher.ViewModels;

namespace RepoNLauncher.Converters;

/// <summary>A small preview of an image file, for attachments waiting to be sent.</summary>
public class ImageThumbnailConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string path ? UserChatItem.Thumbnail(() => File.OpenRead(path), 96) : null;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
