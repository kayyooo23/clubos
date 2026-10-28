using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using ClubOS.Shared;

namespace ClubOS.Service.Sync;

public sealed class SyncException(string message) : Exception(message);

/// <summary>Сколько сделано: на месте и проверено, скачано по сети (из них у других получателей), число файлов.</summary>
public sealed record SyncStats(long BytesDone, long BytesTotal, long BytesFetched, long BytesFromPeers, int FilesDone, int FilesTotal);

public sealed record SyncOutcome(bool Ok, string? Error, SyncStats Stats);

/// <summary>
/// Получатель: приводит свою игровую папку в соответствие со списком файлов. Для каждого файла:
///  • такой же размер и время изменения, как в списке: файл уже на месте, ничего не читается;
///  • иначе строится новая копия блок за блоком: блок, который у нас уже есть и хеш совпал, берётся с диска, остальные скачиваются у других участников
///    (сначала у получателей, потом у эталона, чтобы не нагружать его одного), и каждый проверяется по хешу из списка;
///  • готовая копия получает время изменения из списка и заменяет старый файл одним переименованием: оборванная работа не оставляет битую игру.
/// Скачанное без проверки в игру не попадает. После всех файлов, если задано, удаляются файлы выбранных папок, которых нет в списке (точное зеркало).
/// </summary>
public sealed class SyncDownloader
{
    public const string PartSuffix = ".clubos-part";

    private readonly HttpClient _http;
    private readonly string _root;
    private readonly int _parallelFiles;
    private readonly Func<string, long> _freeSpace;
    private readonly Random _random;

    private long _done, _fetched, _fromPeers;
    private int _filesDone;

    public SyncDownloader(HttpClient http, string root, int parallelFiles = 3, Func<string, long>? freeSpace = null, Random? random = null)
    {
        _http = http;
        _root = root;
        _parallelFiles = Math.Max(1, parallelFiles);
        _freeSpace = freeSpace ?? (path => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace);
        _random = random ?? Random.Shared;
    }

    public static bool IsLanUrl(string? url)
    {
        if (url is null || !url.StartsWith("http://", StringComparison.Ordinal)) return false;
        var rest = url["http://".Length..];
        var colon = rest.LastIndexOf(':');
        if (colon < 0 || !IPAddress.TryParse(rest[..colon], out var ip) || !int.TryParse(rest[(colon + 1)..], out var port) || port is < 1 or > 65535) return false;
        var b = ip.GetAddressBytes();
        return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
               && (b[0] == 10 || b[0] == 127 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168));
    }

    /// <summary>Список файлов берётся у любого участника, но принимается, только если его sha256 совпал с тем, что сообщил сервер.</summary>
    public async Task<(SyncManifest? Manifest, byte[]? Bytes, string? Error)> FetchManifestAsync(int jobId, string token, string expectedSha256, IReadOnlyList<SyncPeer> peers, CancellationToken ct)
    {
        string? last = null;
        foreach (var peer in Order(peers))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{peer.Url}/sync/{jobId}/manifest");
                req.Headers.Add("X-Sync-Token", token);
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) { last = $"{peer.Url}: ответ {(int)resp.StatusCode}"; continue; }
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (!SyncManifest.Digest(bytes).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)) { last = "список файлов не совпал с сообщённым сервером"; continue; }
                var manifest = SyncManifest.Parse(bytes, out var error);
                if (manifest is null) { last = error; continue; }
                return (manifest, bytes, null);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested) { last = "нет связи с участником"; }
        }
        return (null, null, last is null ? "Нет участников, у которых можно взять список файлов" : $"Не удалось получить список файлов: {last}");
    }

    private IEnumerable<SyncPeer> Order(IEnumerable<SyncPeer> peers)
    {
        var usable = peers.Where(p => IsLanUrl(p.Url)).ToList();
        var others = usable.Where(p => p.Role != "source").OrderBy(_ => _random.Next()).ToList();     // сначала получатели: эталон разгружаем
        return others.Concat(usable.Where(p => p.Role == "source"));
    }

    private static long Mtime(FileInfo info) => new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();

    private bool IsInPlace(ManifestFile f, string dest)
    {
        var info = new FileInfo(dest);
        return info.Exists && info.Length == f.Size && Mtime(info) == f.Mtime;
    }

    public async Task<SyncOutcome> RunAsync(int jobId, string token, SyncManifest manifest, IReadOnlyList<string> roots, bool deleteExtra, Func<IReadOnlyList<SyncPeer>> peers,
        Action<SyncStats> onProgress, Func<CancellationToken, Task>? gate, CancellationToken ct)
    {
        _done = _fetched = _fromPeers = 0;
        _filesDone = 0;
        SyncStats Snapshot() => new(Interlocked.Read(ref _done), manifest.TotalBytes, Interlocked.Read(ref _fetched), Interlocked.Read(ref _fromPeers), Volatile.Read(ref _filesDone), manifest.Files.Count);
        try
        {
            var plan = new List<(ManifestFile File, string Dest)>();
            foreach (var f in manifest.Files)
            {
                var dest = SyncPath.Resolve(_root, f.Path);
                if (dest is null) throw new SyncException($"Недопустимый путь в списке: {f.Path}");
                plan.Add((f, dest));
            }
            RemoveLeftovers(roots);

            var todo = new List<(ManifestFile File, string Dest)>();
            foreach (var item in plan)
            {
                if (IsInPlace(item.File, item.Dest)) { Interlocked.Add(ref _done, item.File.Size); Interlocked.Increment(ref _filesDone); }
                else todo.Add(item);
            }
            CheckSpace(todo);
            onProgress(Snapshot());

            var shuffled = todo.OrderBy(_ => _random.Next()).ToList();                   // у каждого получателя свой порядок: у них оказываются разные файлы, и они делятся друг с другом
            await Parallel.ForEachAsync(shuffled, new ParallelOptions { MaxDegreeOfParallelism = _parallelFiles, CancellationToken = ct }, async (item, token2) =>
            {
                await FetchFileAsync(jobId, token, manifest, item.File, item.Dest, peers, () => onProgress(Snapshot()), gate, token2).ConfigureAwait(false);
                Interlocked.Increment(ref _filesDone);
                onProgress(Snapshot());
            }).ConfigureAwait(false);

            if (deleteExtra) DeleteExtra(manifest, roots);
            return new SyncOutcome(true, null, Snapshot());
        }
        catch (SyncException e) { return new SyncOutcome(false, e.Message, Snapshot()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new SyncOutcome(false, $"Ошибка диска: {e.Message}", Snapshot()); }
    }

    private void CheckSpace(List<(ManifestFile File, string Dest)> todo)
    {
        if (todo.Count == 0) return;
        long growth = 0, largest = 0;
        foreach (var (f, dest) in todo)
        {
            var existing = File.Exists(dest) ? new FileInfo(dest).Length : 0;
            growth += Math.Max(0, f.Size - existing);
            largest = Math.Max(largest, f.Size);
        }
        var need = growth + largest * _parallelFiles + (1L << 30);                          // прирост, временные копии больших файлов и запас
        var free = _freeSpace(_root);
        if (free < need) throw new SyncException($"Не хватает места на диске: нужно около {need >> 20} МБ, свободно {free >> 20} МБ");
    }

    private async Task FetchFileAsync(int jobId, string token, SyncManifest manifest, ManifestFile f, string dest, Func<IReadOnlyList<SyncPeer>> peers,
        Action progress, Func<CancellationToken, Task>? gate, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        // Содержимое уже то, что нужно, а отличается только время изменения (например, игру уже обновил сам Steam): переписывать файл незачем, достаточно выставить время.
        if (File.Exists(dest) && new FileInfo(dest).Length == f.Size && await ContentMatchesAsync(dest, f, manifest.BlockSize, gate, ct).ConfigureAwait(false))
        {
            try
            {
                File.SetLastWriteTimeUtc(dest, DateTimeOffset.FromUnixTimeSeconds(f.Mtime).UtcDateTime);
                Interlocked.Add(ref _done, f.Size);
                progress();
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* файл занят: идём обычным путём ниже */ }
        }
        var part = dest + PartSuffix;
        FileStream? old = null;
        try { if (File.Exists(dest)) old = new FileStream(dest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { old = null; }
        try
        {
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var local = new byte[manifest.BlockSize];
                for (var i = 0; i < f.Blocks.Length; i++)
                {
                    if (gate is not null) await gate(ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    var offset = (long)i * manifest.BlockSize;
                    var length = (int)Math.Min(manifest.BlockSize, f.Size - offset);
                    if (old is not null && offset + length <= old.Length)
                    {
                        old.Position = offset;
                        await old.ReadExactlyAsync(local.AsMemory(0, length), ct).ConfigureAwait(false);
                        if (HashOf(local, length) == f.Blocks[i])
                        {
                            await output.WriteAsync(local.AsMemory(0, length), ct).ConfigureAwait(false);       // этот блок у нас уже есть
                            Interlocked.Add(ref _done, length);
                            progress();
                            continue;
                        }
                    }
                    var (data, fromPeer) = await FetchBlockAsync(jobId, token, f, i, offset, length, peers, ct).ConfigureAwait(false);
                    await output.WriteAsync(data.AsMemory(0, length), ct).ConfigureAwait(false);
                    Interlocked.Add(ref _done, length);
                    Interlocked.Add(ref _fetched, length);
                    if (fromPeer) Interlocked.Add(ref _fromPeers, length);
                    progress();
                }
            }
            old?.Dispose();
            old = null;
            File.SetLastWriteTimeUtc(part, DateTimeOffset.FromUnixTimeSeconds(f.Mtime).UtcDateTime);
            File.Move(part, dest, overwrite: true);                                                              // одним действием: игра либо старая, либо новая целиком
        }
        catch
        {
            old?.Dispose();
            try { File.Delete(part); } catch (Exception) { /* останется временный файл: уберётся при следующем запуске */ }
            throw;
        }
    }

    /// <summary>Все блоки файла на диске совпали с хешами из списка (читается один раз, на первом несовпадении останавливается).</summary>
    private static async Task<bool> ContentMatchesAsync(string path, ManifestFile f, int blockSize, Func<CancellationToken, Task>? gate, CancellationToken ct)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
            if (stream.Length != f.Size) return false;
            var buffer = new byte[blockSize];
            for (var i = 0; i < f.Blocks.Length; i++)
            {
                if (gate is not null) await gate(ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                var length = (int)Math.Min(blockSize, f.Size - (long)i * blockSize);
                await stream.ReadExactlyAsync(buffer.AsMemory(0, length), ct).ConfigureAwait(false);
                if (HashOf(buffer, length) != f.Blocks[i]) return false;
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string HashOf(byte[] data, int length) => Convert.ToHexString(SHA256.HashData(data.AsSpan(0, length))).ToLowerInvariant();

    private async Task<(byte[] Data, bool FromPeer)> FetchBlockAsync(int jobId, string token, ManifestFile f, int index, long offset, int length,
        Func<IReadOnlyList<SyncPeer>> peers, CancellationToken ct)
    {
        string? lastError = null;
        foreach (var peer in Order(peers()))
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, $"{peer.Url}/sync/{jobId}/file?path={Uri.EscapeDataString(f.Path)}");
                    req.Headers.Add("X-Sync-Token", token);
                    req.Headers.Range = new RangeHeaderValue(offset, offset + length - 1);
                    using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.RequestedRangeNotSatisfiable) { lastError = "у участника нет этого файла"; break; }
                    if (resp.StatusCode != HttpStatusCode.PartialContent) { lastError = $"ответ {(int)resp.StatusCode}"; continue; }
                    var data = new byte[length];
                    await using (var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                        await body.ReadExactlyAsync(data, ct).ConfigureAwait(false);
                    if (HashOf(data, length) != f.Blocks[index]) { lastError = "блок не прошёл проверку"; break; }   // участник отдал не то: пробуем следующего
                    return (data, peer.Role != "source");
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or EndOfStreamException && !ct.IsCancellationRequested)
                {
                    lastError = "нет связи с участником";
                    await Task.Delay(150, ct).ConfigureAwait(false);
                }
            }
        }
        throw new SyncException(lastError is null ? "Нет участников, у которых можно взять файлы" : $"Не удалось получить блок {index + 1} файла {Path.GetFileName(f.Path)}: {lastError}");
    }

    /// <summary>Оборванная прошлая работа оставляет временные файлы .clubos-part: убираем их в выбранных папках.</summary>
    private void RemoveLeftovers(IReadOnlyList<string> roots)
    {
        foreach (var rel in roots)
        {
            var full = SyncPath.Resolve(_root, rel);
            if (full is null || !Directory.Exists(full)) continue;
            foreach (var file in Directory.EnumerateFiles(full, "*" + PartSuffix, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                try { File.Delete(file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* занят: не страшно */ }
        }
    }

    /// <summary>Точное зеркало: в выбранных папках удаляется всё, чего нет в списке. Ссылки не затрагиваются, за пределы выбранных папок не выходим.</summary>
    private void DeleteExtra(SyncManifest manifest, IReadOnlyList<string> roots)
    {
        var keep = new HashSet<string>(manifest.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var rel in roots)
        {
            var full = SyncPath.Resolve(_root, rel);
            if (full is null || !Directory.Exists(full)) continue;
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var file in Directory.EnumerateFiles(full, "*", options).ToList())
                if (!keep.Contains(SyncPath.Relative(_root, file)))
                    try { File.Delete(file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* занят: останется */ }
            foreach (var dir in Directory.EnumerateDirectories(full, "*", options).OrderByDescending(d => d.Length).ToList())
                try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* не пустая или занята */ }
        }
    }
}
