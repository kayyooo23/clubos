using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClubOS.Shared.Native;
using ClubOS.Shared;

namespace ClubOS.Service.Sync;

/// <summary>Один файл списка: путь внутри игровой папки, размер, время изменения (секунды UTC) и sha256 каждого блока (последний блок может быть короче).</summary>
public sealed record ManifestFile(string Path, long Size, long Mtime, string[] Blocks);

/// <summary>
/// Список файлов задания с хешами блоков. Его считает эталонный ПК, а sha256 самого списка сообщает серверу: получатели скачивают список у любого участника
/// и сверяют его sha256 с тем, что сказал сервер, поэтому подделать список другой участник не может. Блоки нужны для двух вещей: проверить каждый скачанный кусок
/// и скачивать только изменившиеся блоки (игра обновилась, а большая часть её файлов осталась прежней).
/// </summary>
public sealed record SyncManifest(int Version, int BlockSize, IReadOnlyList<ManifestFile> Files)
{
    public const int CurrentVersion = 1;
    public const int DefaultBlockSize = 4 * 1024 * 1024;
    public const int MinBlockSize = 1024;
    public const int MaxBlockSize = 64 * 1024 * 1024;
    public const int MaxFiles = 5_000_000;

    private static readonly Regex Sha256Hex = new("^[0-9a-f]{64}$", RegexOptions.Compiled);

    public long TotalBytes => Files.Sum(f => f.Size);

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Json.Options);

    public static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static int BlockCount(long size, int blockSize) => (int)((size + blockSize - 1) / blockSize);

    /// <summary>Разбор списка, пришедшего от другого ПК. Всё, что выходит за разумные рамки (чужой формат, лишние блоки, опасный путь), отвергается целиком.</summary>
    public static SyncManifest? Parse(byte[] bytes, out string? error)
    {
        error = null;
        SyncManifest? m;
        try { m = JsonSerializer.Deserialize<SyncManifest>(bytes, Json.Options); }
        catch (JsonException) { error = "Список файлов не читается"; return null; }
        if (m is null || m.Version != CurrentVersion) { error = "Неизвестная версия списка файлов"; return null; }
        if (m.BlockSize is < MinBlockSize or > MaxBlockSize) { error = "Недопустимый размер блока"; return null; }
        if (m.Files is null || m.Files.Count > MaxFiles) { error = "Недопустимое число файлов в списке"; return null; }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in m.Files)
        {
            if (SyncPath.Normalize(f.Path) != f.Path) { error = $"Опасный или неверный путь в списке: {Short(f.Path)}"; return null; }
            if (!seen.Add(f.Path)) { error = $"Повтор в списке: {Short(f.Path)}"; return null; }
            if (f.Size < 0 || f.Blocks is null || f.Blocks.Length != BlockCount(f.Size, m.BlockSize) || f.Blocks.Any(b => b is null || !Sha256Hex.IsMatch(b)))
            { error = $"Неверные блоки у файла: {Short(f.Path)}"; return null; }
        }
        return m;
    }

    private static string Short(string? s) => s is null ? "" : s.Length <= 60 ? s : s[..60];
}

/// <summary>Игра Steam: папка в steamapps/common и её файл appmanifest_ID.acf. Без этого файла Steam не знает, что игра установлена, поэтому он копируется вместе с папкой.</summary>
public static class SteamLibrary
{
    private static readonly Regex InstallDir = new("\"installdir\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>installdir из appmanifest (или null).</summary>
    public static string? ReadInstallDir(string acfPath)
    {
        try
        {
            using var reader = new StreamReader(new FileStream(acfPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            var buffer = new char[16 * 1024];
            var read = reader.Read(buffer, 0, buffer.Length);
            return InstallDir.Match(new string(buffer, 0, read)) is { Success: true } m ? m.Groups[1].Value : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Для путей вида steamapps/common/ИГРА: пути их appmanifest, которые есть в игровой папке.</summary>
    public static IReadOnlyList<string> ExtraFiles(string root, IEnumerable<string> paths)
    {
        var common = SyncPath.Resolve(root, "steamapps");
        if (common is null || !Directory.Exists(common)) return [];
        var wanted = new HashSet<string>(paths.Select(p => SyncPath.Normalize(p)).Where(p => p is not null).Select(p => p!)
            .Where(p => p.Split('/') is [var a, var b, _] && a.Equals("steamapps", StringComparison.OrdinalIgnoreCase) && b.Equals("common", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Split('/')[2]), StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return [];
        var found = new List<string>();
        foreach (var acf in Directory.EnumerateFiles(common, "appmanifest_*.acf"))
            if (ReadInstallDir(acf) is { } dir && wanted.Contains(dir)) found.Add("steamapps/" + Path.GetFileName(acf));
        return found;
    }
}

/// <summary>Считает список файлов и хеши блоков. Файлы читаются без блокировки (Steam может быть запущен), хеши прежнего списка переиспользуются, если файл не менялся.</summary>
public sealed class ManifestBuilder(string root, int blockSize = SyncManifest.DefaultBlockSize, int parallelism = 2)
{
    public sealed record Result(SyncManifest? Manifest, string? Error);

    public async Task<Result> BuildAsync(IReadOnlyList<string> paths, IReadOnlyDictionary<string, ManifestFile>? previous, Action<long, long>? progress, CancellationToken ct)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);          // относительный путь, полный путь
        var wanted = paths.Concat(SteamLibrary.ExtraFiles(root, paths)).ToList();
        foreach (var path in wanted)
        {
            var full = SyncPath.Resolve(root, path);
            if (full is null) return new(null, $"Недопустимый путь: {path}");
            if (File.Exists(full)) files[SyncPath.Relative(root, full)] = full;
            else if (Directory.Exists(full))
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                foreach (var file in Directory.EnumerateFiles(full, "*", options))
                {
                    var rel = SyncPath.Relative(root, file);
                    if (SyncPath.Normalize(rel) != rel) return new(null, $"Файл с недопустимым для копирования именем: {rel}");
                    files[rel] = file;
                }
            }
            else return new(null, $"Не найдено на эталонном ПК: {path}");
        }
        if (files.Count == 0) return new(null, "В выбранных папках нет файлов");

        var infos = files.Select(kv => (Rel: kv.Key, Full: kv.Value, Info: new FileInfo(kv.Value))).ToList();
        var total = infos.Sum(i => i.Info.Length);
        long done = 0;
        var results = new ManifestFile[infos.Count];
        string? failure = null;
        try
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, infos.Count), new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct }, async (i, token) =>
            {
                var (rel, full, info) = infos[i];
                var mtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();
                if (previous is not null && previous.TryGetValue(rel, out var old) && old.Size == info.Length && old.Mtime == mtime && old.Blocks.Length == SyncManifest.BlockCount(info.Length, blockSize))
                {
                    results[i] = old;                                                          // файл не менялся: хеши прежние
                }
                else
                {
                    var blocks = await HashBlocksAsync(full, info.Length, token).ConfigureAwait(false);
                    results[i] = new ManifestFile(rel, info.Length, mtime, blocks);
                }
                progress?.Invoke(Interlocked.Add(ref done, info.Length), total);
            }).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            failure = e.Message;
        }
        if (failure is not null) return new(null, failure);
        var sorted = results.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        return new(new SyncManifest(SyncManifest.CurrentVersion, blockSize, sorted), null);
    }

    private async Task<string[]> HashBlocksAsync(string full, long length, CancellationToken ct)
    {
        if (NativeBlockHasher.IsAvailable)                 // чтение и хеширование в нативной части (C++): быстрее, результат тот же
        {
            var digests = await Task.Run(() => NativeBlockHasher.HashBlocks(full, length, blockSize, ct), ct).ConfigureAwait(false);
            return Enumerable.Range(0, digests.Length / NativeBlockHasher.DigestSize).Select(i => NativeBlockHasher.HexOf(digests, i)).ToArray();
        }
        return await HashBlocksManagedAsync(full, length, ct).ConfigureAwait(false);
    }

    /// <summary>Тот же подсчёт на C#: запасной путь, если нативной библиотеки нет рядом со службой, и эталон для проверки нативной.</summary>
    internal async Task<string[]> HashBlocksManagedAsync(string full, long length, CancellationToken ct)
    {
        var blocks = new string[SyncManifest.BlockCount(length, blockSize)];
        await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
        var buffer = new byte[blockSize];
        for (var i = 0; i < blocks.Length; i++)
        {
            var want = (int)Math.Min(blockSize, length - (long)i * blockSize);
            var read = 0;
            while (read < want)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read, want - read), ct).ConfigureAwait(false);
                if (n == 0) throw new InvalidDataException($"Файл изменился во время подсчёта: {Path.GetFileName(full)}");
                read += n;
            }
            blocks[i] = Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, want))).ToLowerInvariant();
        }
        if (stream.Length != length) throw new InvalidDataException($"Файл изменился во время подсчёта: {Path.GetFileName(full)}");
        return blocks;
    }
}

/// <summary>Игра, найденная в игровой папке: путь для задания, название, вид (dir или steam), размер и число файлов.</summary>
public sealed record GameEntryDto(string Path, string Name, string Kind, long SizeBytes, int Files);

/// <summary>Что лежит в игровой папке: папки верхнего уровня, а для библиотеки Steam игры из steamapps/common. Из этого списка администратор выбирает, что копировать на другие ПК.</summary>
public static class GamesInventory
{
    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase) { "$RECYCLE.BIN", "System Volume Information", "steamapps", "Config.Msi", "Recovery" };
    public const int MaxEntries = 3000;

    public static IReadOnlyList<GameEntryDto> Scan(string root, CancellationToken ct = default)
    {
        var entries = new List<GameEntryDto>();
        if (!Directory.Exists(root)) return entries;
        foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(dir);
            if (Skip.Contains(name) || name.StartsWith('.') || name.StartsWith('$') || IsLink(dir)) continue;
            entries.Add(Describe(dir, name, name, "dir", ct));
            if (entries.Count >= MaxEntries) return entries;
        }
        var common = Path.Combine(root, "steamapps", "common");
        if (Directory.Exists(common) && !IsLink(Path.Combine(root, "steamapps")) && !IsLink(common))
            foreach (var dir in Directory.EnumerateDirectories(common).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(dir);
                if (IsLink(dir)) continue;
                entries.Add(Describe(dir, name, "steamapps/common/" + name, "steam", ct));
                if (entries.Count >= MaxEntries) break;
            }
        return entries;
    }

    private static bool IsLink(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
    }

    private static GameEntryDto Describe(string dir, string name, string path, string kind, CancellationToken ct)
    {
        long size = 0;
        var files = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*", options))
        {
            ct.ThrowIfCancellationRequested();
            size += file.Length;
            files++;
        }
        return new GameEntryDto(path, name, kind, size, files);
    }
}
