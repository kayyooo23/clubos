using System.IO.Compression;
using System.Reflection;
using ClubOS.Shared;

namespace ClubOS.Service.Update;

/// <summary>
/// Распаковка пакета обновления. Пакет уже проверен подписью, но распаковываем всё равно осторожно: служба работает от имени SYSTEM.
/// Принимаются только папки Service и Overlay и файл version.txt; пути с «..», абсолютные, с двоеточием (диск, поток NTFS) отвергаются;
/// число и размер файлов ограничены, размер считается по фактически записанным байтам (заявленному в архиве не верим).
/// </summary>
public static class SafeUnzip
{
    public sealed record Limits(int MaxEntries = 20_000, long MaxTotalBytes = 1_500_000_000, long MaxEntryBytes = 600_000_000);

    /// <summary>null: распаковано. Иначе текст ошибки; частично распакованное остаётся, вызывающий удаляет папку.</summary>
    public static string? Extract(string zipPath, string destination, Limits? limits = null)
    {
        limits ??= new Limits();
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            if (zip.Entries.Count == 0) return "Пакет пустой";
            if (zip.Entries.Count > limits.MaxEntries) return "В пакете слишком много файлов";
            var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            long declared = 0;
            foreach (var e in zip.Entries)
            {
                var name = e.FullName.Replace('\\', '/');
                if (name.Length == 0) continue;
                if (name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(p => p == ".."))
                    return $"Недопустимый путь в пакете: {Shorten(e.FullName)}";
                var top = name.Split('/')[0];
                if (!(top is "Service" or "Overlay" || name == "version.txt"))
                    return $"Недопустимое содержимое пакета: {Shorten(e.FullName)}";
                if (!Path.GetFullPath(Path.Combine(destination, name)).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return $"Недопустимый путь в пакете: {Shorten(e.FullName)}";
                declared += e.Length;
                if (e.Length > limits.MaxEntryBytes || declared > limits.MaxTotalBytes) return "Пакет слишком большой после распаковки";
            }
            Directory.CreateDirectory(destination);
            long written = 0;
            foreach (var e in zip.Entries)
            {
                var name = e.FullName.Replace('\\', '/');
                if (name.Length == 0) continue;
                var full = Path.GetFullPath(Path.Combine(destination, name));
                if (name.EndsWith('/')) { Directory.CreateDirectory(full); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                using var source = e.Open();
                using var target = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                long entry = 0;
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    entry += read;
                    written += read;
                    if (entry > limits.MaxEntryBytes || written > limits.MaxTotalBytes) return "Пакет слишком большой после распаковки";
                    target.Write(buffer, 0, read);
                }
            }
            return null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return $"Пакет не распаковался: {Shorten(ex.Message)}";
        }
    }

    private static string Shorten(string text) => text.Length <= 80 ? text : text[..80];
}

/// <summary>
/// Проверка распакованной версии: на месте служба и оверлей, version.txt и версия самих программ совпадают с подписанной. Версия читается из метаданных управляемых
/// сборок (ClubOS.Service.dll, ClubOS.Overlay.dll): это не запускает код и, в отличие от чтения ресурсов exe средствами Windows, не зависит от длины пути (там предел 260 знаков).
/// </summary>
public sealed class PackageValidator(Func<string, string?>? readVersion = null)
{
    private readonly Func<string, string?> _assemblyVersion = readVersion ?? ManagedVersion;

    private static string? ManagedVersion(string path)
    {
        try { return AssemblyName.GetAssemblyName(path).Version?.ToString(); }
        catch (Exception e) when (e is BadImageFormatException or IOException or FileLoadException or UnauthorizedAccessException) { return null; }
    }

    public string? Validate(string dir, string version)
    {
        var service = InstallLayout.ServiceExe(dir);
        var overlay = InstallLayout.OverlayExe(dir);
        var serviceDll = Path.ChangeExtension(service, ".dll");
        var overlayDll = Path.ChangeExtension(overlay, ".dll");
        if (!File.Exists(service) || !File.Exists(overlay) || !File.Exists(serviceDll) || !File.Exists(overlayDll)) return "В пакете нет службы или оверлея";
        var stampFile = Path.Combine(dir, "version.txt");
        var stamp = File.Exists(stampFile) ? AppVersion.Normalize(File.ReadAllText(stampFile)) : null;
        if (stamp != AppVersion.Normalize(version)) return "Версия в пакете не совпадает с заявленной";
        foreach (var dll in new[] { serviceDll, overlayDll })
        {
            var found = _assemblyVersion(dll);
            if (AppVersion.Normalize(found) != AppVersion.Normalize(version))
                return $"Версия программы в пакете не совпадает с заявленной: {Path.GetFileName(dll)} имеет версию «{found ?? "нет"}», ожидалась {version}";
        }
        return null;
    }
}
