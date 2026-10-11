namespace ClubOS.Shared;

/// <summary>
/// Картинки, которые служба скачала на ПК (рекламные баннеры и обложки игр) и показала оверлею путём к файлу. Оверлей работает от имени гостя
/// и по каналу получает только пути, поэтому перед показом проверяет: файл лежит внутри своей папки и это png или jpg.
/// </summary>
public static class ImageFiles
{
    /// <summary>Полный путь к файлу, если он внутри папки dir и его расширение png/jpg/jpeg; иначе null (в том числе для «..», чужих папок и пустых путей).</summary>
    public static string? Safe(string? path, string dir)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string full, root;
        try
        {
            full = Path.GetFullPath(path);
            root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        var ext = Path.GetExtension(full).ToLowerInvariant();
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && ext is ".png" or ".jpg" or ".jpeg" ? full : null;
    }
}
