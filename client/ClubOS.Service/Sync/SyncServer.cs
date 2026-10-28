using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace ClubOS.Service.Sync;

/// <summary>
/// Раздача файлов по локальной сети: эталонный ПК и каждый получатель отдают другим участникам задания то, что у них уже есть. Две операции:
///   GET /sync/{задание}/manifest        список файлов (его sha256 получатель сверяет с тем, что сказал сервер);
///   GET /sync/{задание}/file?path=...   файл или его кусок (Range), только из списка и только если на диске он ровно такой, как в списке (размер и время).
/// Каждый запрос должен нести секрет задания (X-Sync-Token), который знают только участники. Отдаются исключительно файлы из списка задания: по произвольному
/// пути ничего не прочитать. Получатель всё равно проверяет хеш каждого блока, поэтому подмена данных по дороге или у раздающего ничего не портит.
/// </summary>
public sealed class SyncServer : IDisposable
{
    private sealed record Served(string Token, SyncManifest Manifest, byte[] ManifestBytes, Dictionary<string, ManifestFile> ByPath);

    private readonly ConcurrentDictionary<int, Served> _jobs = new();
    private readonly HttpListener _listener = new();
    private readonly SemaphoreSlim _slots = new(32);
    private readonly CancellationTokenSource _stop = new();
    private readonly string _root;
    private readonly Action<string>? _log;
    private bool _started;

    public int Port { get; }

    /// <param name="bindHost">«+» (все адреса, для службы от имени системы) или «127.0.0.1» (разработка и тесты: не требует прав).</param>
    public SyncServer(string root, string bindHost, int port, Action<string>? log = null)
    {
        _root = root;
        Port = port;
        _log = log;
        _listener.Prefixes.Add($"http://{bindHost}:{port}/");
    }

    public bool Started => _started;

    public bool TryStart(out string? error)
    {
        try
        {
            _listener.Start();
            _started = true;
            _ = Task.Run(AcceptLoopAsync);
            error = null;
            return true;
        }
        catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
        {
            error = $"Не удалось занять порт {Port} для раздачи файлов: {e.Message}";
            return false;
        }
    }

    public void Serve(int jobId, string token, SyncManifest manifest, byte[] manifestBytes) =>
        _jobs[jobId] = new Served(token, manifest, manifestBytes, manifest.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase));

    public void Stop(int jobId) => _jobs.TryRemove(jobId, out _);

    public bool IsServing(int jobId) => _jobs.ContainsKey(jobId);

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException) { break; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private static async Task Reply(HttpListenerResponse res, int status, string? text = null)
    {
        res.StatusCode = status;
        if (text is null) { res.ContentLength64 = 0; return; }
        var bytes = Encoding.UTF8.GetBytes(text);
        res.ContentType = "text/plain; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        await _slots.WaitAsync(_stop.Token).ConfigureAwait(false);
        try
        {
            var req = ctx.Request;
            var parts = req.Url!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 || parts[0] != "sync" || !int.TryParse(parts[1], out var id) || req.HttpMethod is not ("GET" or "HEAD")) { await Reply(res, 404); return; }
            if (!_jobs.TryGetValue(id, out var job)) { await Reply(res, 404); return; }
            var given = Encoding.UTF8.GetBytes(req.Headers["X-Sync-Token"] ?? "");
            if (!CryptographicOperations.FixedTimeEquals(given, Encoding.UTF8.GetBytes(job.Token))) { await Reply(res, 401); return; }

            if (parts[2] == "manifest")
            {
                res.ContentType = "application/json";
                res.ContentLength64 = job.ManifestBytes.Length;
                if (req.HttpMethod == "GET") await res.OutputStream.WriteAsync(job.ManifestBytes).ConfigureAwait(false);
                return;
            }
            if (parts[2] != "file") { await Reply(res, 404); return; }
            await SendFileAsync(req, res, job).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            // получатель оборвал связь: ничего страшного
        }
        catch (Exception e)
        {
            _log?.Invoke($"Сбой при раздаче файла: {e.GetType().Name}");
            try { res.StatusCode = 500; } catch (Exception) { /* ответ уже начат */ }
        }
        finally
        {
            _slots.Release();
            try { res.Close(); } catch (Exception) { /* уже закрыт */ }
        }
    }

    private async Task SendFileAsync(HttpListenerRequest req, HttpListenerResponse res, Served job)
    {
        var normal = SyncPath.Normalize(req.QueryString["path"]);
        if (normal is null || !job.ByPath.TryGetValue(normal, out var entry)) { await Reply(res, 404); return; }
        var full = SyncPath.Resolve(_root, entry.Path);
        FileInfo? info = full is null ? null : new FileInfo(full);
        // файл должен быть на диске ровно таким, как в списке: недокачанный или изменившийся не раздаём (у кого его нет, получатель спросит другого)
        if (info is null || !info.Exists || info.Length != entry.Size || new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds() != entry.Mtime) { await Reply(res, 404); return; }

        long from = 0, to = entry.Size - 1;
        var partial = false;
        if (req.Headers["Range"] is { } range)
        {
            if (!TryParseRange(range, entry.Size, out from, out to)) { res.Headers["Content-Range"] = $"bytes */{entry.Size}"; await Reply(res, 416); return; }
            partial = true;
        }
        var length = entry.Size == 0 ? 0 : to - from + 1;
        res.StatusCode = partial ? 206 : 200;
        res.ContentType = "application/octet-stream";
        res.Headers["Accept-Ranges"] = "bytes";
        if (partial) res.Headers["Content-Range"] = $"bytes {from}-{to}/{entry.Size}";
        res.ContentLength64 = length;
        if (req.HttpMethod == "HEAD" || length == 0) return;

        await using var file = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
        file.Position = from;
        var buffer = new byte[256 * 1024];
        var left = length;
        while (left > 0)
        {
            var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), _stop.Token).ConfigureAwait(false);
            if (read == 0) break;
            await res.OutputStream.WriteAsync(buffer.AsMemory(0, read), _stop.Token).ConfigureAwait(false);
            left -= read;
        }
    }

    /// <summary>Один диапазон «bytes=a-b», «bytes=a-» или «bytes=-n».</summary>
    public static bool TryParseRange(string header, long size, out long from, out long to)
    {
        from = to = 0;
        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || header.Contains(',')) return false;
        var spec = header["bytes=".Length..].Split('-');
        if (spec.Length != 2 || size == 0) return false;
        if (spec[0] == "")
        {
            if (!long.TryParse(spec[1], out var suffix) || suffix <= 0) return false;
            from = Math.Max(0, size - suffix);
            to = size - 1;
            return true;
        }
        if (!long.TryParse(spec[0], out from) || from < 0 || from >= size) return false;
        to = spec[1] == "" ? size - 1 : long.TryParse(spec[1], out var end) ? Math.Min(end, size - 1) : -1;
        return to >= from;
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { if (_started) _listener.Stop(); _listener.Close(); } catch (Exception) { /* уже остановлен */ }
        _jobs.Clear();
    }
}
