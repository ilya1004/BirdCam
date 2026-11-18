using System.IO;
using System.Windows.Media.Imaging;

namespace BirdCam.UI.Imaging;

internal static class ImageLoader
{
    /// <summary>Загружает картинку без блокировки файла.</summary>
    public static BitmapImage? Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path!, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static BitmapSource CreateBitmap(byte[] pixels, int width, int height, int stride)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgr24, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }
}

