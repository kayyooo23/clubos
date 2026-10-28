using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ClubOS.Shared;

/// <summary>Результат вызова сервера. Status = 0 означает «до сервера не достучались».</summary>
public sealed record ApiResult<T>(bool Ok, int Status, T? Value, string? Error, string? Code = null)
{
    public bool NetworkError => Status == 0;
}

/// <summary>
/// Тонкая обёртка над HTTP API сервера. Ключ ПК и отпечаток железа добавляются ко всем запросам; токен — по требованию.
/// Ключ можно задать позже (SetKey): ПК, подключаемый по коду, получает его от сервера уже после запуска службы.
/// </summary>
public sealed class ServerApi
{
    private readonly HttpClient _http;
    private readonly string? _machineId;
    private volatile string? _pcKey;

    public ServerApi(HttpClient http, string? pcKey, string? machineId = null)
    {
        _http = http;
        _pcKey = string.IsNullOrWhiteSpace(pcKey) ? null : pcKey;
        _machineId = string.IsNullOrWhiteSpace(machineId) ? null : machineId;
    }

    public bool HasKey => _pcKey is not null;

    public void SetKey(string? key) => _pcKey = string.IsNullOrWhiteSpace(key) ? null : key;

    private void Sign(HttpRequestMessage req)
    {
        if (_pcKey is { } key) req.Headers.TryAddWithoutValidation("X-PC-Key", key);
        if (_machineId is { } machine) req.Headers.TryAddWithoutValidation("X-Machine-Id", machine);
    }

    public Task<ApiResult<T>> GetAsync<T>(string path, string? token = null, CancellationToken ct = default)
        => SendAsync<T>(HttpMethod.Get, path, null, token, ct);

    public Task<ApiResult<T>> PostAsync<T>(string path, object? body = null, string? token = null, CancellationToken ct = default)
        => SendAsync<T>(HttpMethod.Post, path, body ?? new { }, token, ct);

    public async Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string path, object? body, string? token, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(method, path);
            Sign(req);
            if (body is not null) req.Content = JsonContent.Create(body, options: Json.Options);
            if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                try { return new(true, (int)resp.StatusCode, JsonSerializer.Deserialize<T>(raw, Json.Options), null); }
                catch (JsonException) { return new(false, (int)resp.StatusCode, default, "Сервер вернул непонятный ответ"); }
            }
            return new(false, (int)resp.StatusCode, default, ExtractError(raw, resp.StatusCode), ExtractCode(raw));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return new(false, 0, default, "Нет связи с сервером");
        }
    }

    /// <summary>Скачивает файл целиком, но не больше maxBytes: слишком большой ответ отвергается, не заполняя память.</summary>
    public async Task<ApiResult<byte[]>> GetBytesAsync(string path, int maxBytes, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, path);
            Sign(req);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var status = (int)resp.StatusCode;
            if (!resp.IsSuccessStatusCode)
                return new(false, status, null, ExtractError(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false), resp.StatusCode));
            if (resp.Content.Headers.ContentLength > maxBytes) return new(false, status, null, "Файл слишком большой");
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maxBytes) return new(false, status, null, "Файл слишком большой");
                buffer.Write(chunk, 0, read);
            }
            return new(true, status, buffer.ToArray(), null);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return new(false, 0, null, "Нет связи с сервером");
        }
    }

    /// <summary>
    /// Скачивает большой файл (пакет обновления) потоком прямо на диск, не держа его в памяти, и считает sha256 на лету.
    /// Не дольше maxBytes; если данные перестали приходить на stallSeconds секунд, загрузка обрывается (общий таймаут HttpClient на неё не действует).
    /// </summary>
    public async Task<ApiResult<(string Sha256, long Size)>> DownloadToFileAsync(string path, string destination, long maxBytes, int stallSeconds = 60, CancellationToken ct = default)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            stall.CancelAfter(TimeSpan.FromSeconds(stallSeconds));
            using var req = new HttpRequestMessage(HttpMethod.Get, path);
            Sign(req);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            var status = (int)resp.StatusCode;
            if (!resp.IsSuccessStatusCode)
                return new(false, status, default, ExtractError(await resp.Content.ReadAsStringAsync(stall.Token).ConfigureAwait(false), resp.StatusCode));
            if (resp.Content.Headers.ContentLength > maxBytes) return new(false, status, default, "Файл слишком большой");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var hash = System.Security.Cryptography.SHA256.Create();
            long total = 0;
            await using (var source = await resp.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false))
            await using (var file = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var chunk = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(chunk, stall.Token).ConfigureAwait(false)) > 0)
                {
                    stall.CancelAfter(TimeSpan.FromSeconds(stallSeconds));      // данные идут: отсчёт простоя начинается заново
                    total += read;
                    if (total > maxBytes)
                    {
                        file.Close();
                        File.Delete(destination);
                        return new(false, status, default, "Файл слишком большой");
                    }
                    hash.TransformBlock(chunk, 0, read, null, 0);
                    await file.WriteAsync(chunk.AsMemory(0, read), stall.Token).ConfigureAwait(false);
                }
            }
            hash.TransformFinalBlock([], 0, 0);
            return new(true, status, (Convert.ToHexString(hash.Hash!).ToLowerInvariant(), total), null);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            try { File.Delete(destination); } catch (IOException) { /* останется недокачанный файл: его перезапишет следующая попытка */ }
            return new(false, 0, default, "Нет связи с сервером");
        }
    }

    /// <summary>Машинный код ошибки, если сервер его прислал: {"detail": "...", "code": "machine_mismatch"}.</summary>
    internal static string? ExtractCode(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Сервер отвечает {"detail": "текст"} (наши ошибки) или {"detail": [{"msg": ...}]} (ошибки проверки данных).</summary>
    internal static string ExtractError(string raw, HttpStatusCode status)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("detail", out var d))
            {
                if (d.ValueKind == JsonValueKind.String) return d.GetString()!;
                if (d.ValueKind == JsonValueKind.Array)
                {
                    var msgs = d.EnumerateArray()
                        .Select(x => x.TryGetProperty("msg", out var m) ? m.GetString() : null)
                        .Where(m => !string.IsNullOrWhiteSpace(m))
                        .Select(m => m!.Replace("Value error, ", ""));
                    var joined = string.Join("; ", msgs);
                    if (joined.Length > 0) return joined;
                    return "Проверьте введённые данные";
                }
            }
        }
        catch (JsonException) { /* не JSON — ниже общий текст */ }
        return $"Ошибка сервера ({(int)status})";
    }
}
