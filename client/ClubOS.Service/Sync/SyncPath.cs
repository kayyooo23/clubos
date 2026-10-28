namespace ClubOS.Service.Sync;

/// <summary>
/// Пути в синхронизации приходят снаружи (из списка файлов другого ПК, из задания сервера), поэтому каждый проверяется здесь, прежде чем по нему что-то читается,
/// пишется или удаляется. Допустим только относительный путь внутри игровой папки: через «/», без «..», без диска и потоков NTFS, без недопустимых для Windows знаков
/// и имён устройств (CON, NUL...). Дополнительно путь не должен проходить через ссылку (junction, symlink): по ней можно выйти за пределы игровой папки.
/// </summary>
public static class SyncPath
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly char[] BadChars = ['<', '>', '"', '|', '?', '*', ':'];

    public const int MaxLength = 1024;

    /// <summary>Относительный путь через «/» или null, если путь недопустим.</summary>
    public static string? Normalize(string? relative)
    {
        var path = relative?.Trim().Replace('\\', '/');
        if (string.IsNullOrEmpty(path) || path.Length > MaxLength || path.StartsWith('/')) return null;
        var parts = path.TrimEnd('/').Split('/');
        foreach (var part in parts)
        {
            if (part is "" or "." or "..") return null;
            if (part.IndexOfAny(BadChars) >= 0 || part.Any(char.IsControl)) return null;
            if (part != part.TrimEnd(' ', '.')) return null;                           // Windows молча срезает пробелы и точки в конце: это другой файл
            var stem = part.Contains('.') ? part[..part.IndexOf('.')] : part;
            if (Reserved.Contains(stem)) return null;
        }
        return string.Join('/', parts);
    }

    /// <summary>Полный путь внутри root или null: путь недопустим, выходит за root или проходит через ссылку.</summary>
    public static string? Resolve(string root, string? relative)
    {
        var normal = Normalize(relative);
        if (normal is null) return null;
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(rootFull, normal.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        var current = rootFull;
        foreach (var part in full[(rootFull.Length + 1)..].Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            try
            {
                if (!File.Exists(current) && !Directory.Exists(current)) break;          // дальше ничего нет: ссылок тоже
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }
        return full;
    }

    /// <summary>Путь относительно root через «/».</summary>
    public static string Relative(string root, string full) =>
        Path.GetRelativePath(Path.GetFullPath(root), full).Replace(Path.DirectorySeparatorChar, '/');
}
