using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace ClubOS.Overlay;

/// <summary>
/// Путь к файлу обложки → картинка для плитки. Путь уже проверен моделью (внутри папки обложек, png или jpg). Файл читается сразу целиком:
/// служба потом может заменить или удалить его, а плитка на экране не должна от этого потерять картинку. Одна и та же обложка не читается заново:
/// имя файла содержит отпечаток, новая версия приходит под новым именем.
/// </summary>
public sealed class CoverImage : IValueConverter
{
    private const int DecodeWidth = 360;   // плитка около 180 пикселей: вдвое больше хватает и для экрана 4K
    private static readonly Dictionary<string, BitmapImage?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || path.Length == 0) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(path, out var cached)) return cached;
            var image = Load(path);
            if (Cache.Count > 600) Cache.Clear();           // потолок на случай очень длинного каталога и частой смены версий
            Cache[path] = image;
            return image;
        }
    }

    private static BitmapImage? Load(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.DecodePixelWidth = DecodeWidth;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or InvalidOperationException or UriFormatException or ArgumentException or FormatException)
        {
            Log.Error("Обложка не открылась", e);
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
