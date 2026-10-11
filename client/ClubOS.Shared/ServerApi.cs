using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ClubOS.Shared;

/// <summary>Результат вызова сервера. Status = 0 означает «до сервера не достучались».</summary>
public sealed record ApiResult<T>(bool Ok, int Status, T? Value, string? Error)
{
    public bool NetworkError => Status == 0;
}

/// <summary>Тонкая обёртка над HTTP API сервера. Ключ ПК добавляется ко всем запросам; токен — по требованию.</summary>
public sealed class ServerApi
{
    private readonly HttpClient _http;

    public ServerApi(HttpClient http, string pcKey)
    {
        _http = http;
        _http.DefaultRequestHeaders.Remove("X-PC-Key");
        _http.DefaultRequestHeaders.Add("X-PC-Key", pcKey);
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
            if (body is not null) req.Content = JsonContent.Create(body, options: Json.Options);
            if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                try { return new(true, (int)resp.StatusCode, JsonSerializer.Deserialize<T>(raw, Json.Options), null); }
                catch (JsonException) { return new(false, (int)resp.StatusCode, default, "Сервер вернул непонятный ответ"); }
            }
            return new(false, (int)resp.StatusCode, default, ExtractError(raw, resp.StatusCode));
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
