using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ClubOS.Shared;

namespace ClubOS.Service;

/// <summary>
/// Обложки игр на диске ПК. Как и с баннерами, оверлей на сервер не ходит (у него нет ключа ПК), поэтому картинки скачивает служба,
/// проверяет и кладёт в папку, откуда оверлей их только читает. Файл называется «номер-отпечаток.расширение»: заменили обложку на сервере,
/// отпечаток изменился, скачается новая, прежняя удалится. Принимаем PNG и JPEG (тип по первым байтам), не больше 2 МБ,
/// и только если SHA-256 скачанного совпал с отпечатком из списка игр.
/// </summary>
public sealed partial class CoverCache
{
    public const int MaxBytes = 2 * 1024 * 1024;
    private static readonly string[] Extensions = [".png", ".jpg"];
    private static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(6);   // служба отвечает оверлею не дольше 10 секунд

    private readonly ServerApi _api;
    private readonly string _dir;
    private readonly TimeSpan _budget;
    private readonly Action<string>? _log;

    public CoverCache(ServerApi api, string dir, TimeSpan? budget = null, Action<string>? log = null)
    {
        _api = api;
        _dir = dir;
        _budget = budget ?? DefaultBudget;
        _log = log;
    }

    [GeneratedRegex("^[0-9a-f]{8,64}$")]
    private static partial Regex VersionPattern();

    /// <summary>
    /// У игр, чьи обложки уже на диске, заполняется CoverPath. Что не успело скачаться за отведённое время, возвращается без пути
    /// (меню спросит ещё раз через несколько секунд). Обложки игр, которых больше нет в списке, удаляются; чужие файлы в папке не трогаются.
    /// </summary>
    public async Task<IReadOnlyList<AppInfo>> SyncAsync(IReadOnlyList<AppInfo> apps, CancellationToken ct = default)
    {
        var wanted = apps.Where(a => a.Id > 0 && a.CoverVersion is not null && VersionPattern().IsMatch(a.CoverVersion)).ToList();
        var result = apps.Select(a => a with { CoverPath = null }).ToList();
        try { Directory.CreateDirectory(_dir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"Папка обложек недоступна ({e.Message})");
            return result;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(_budget);
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new Dictionary<int, string>();
        foreach (var app in wanted)
        {
            var path = FindCached(app);
            if (path is null && !budget.IsCancellationRequested)
            {
                try { path = await DownloadAsync(app, budget.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* вышло время: остальное подхватим в следующий раз */ }
            }
            ct.ThrowIfCancellationRequested();
            if (path is null) continue;
            keep.Add(Path.GetFileName(path));
            paths[app.Id] = path;
        }
        RemoveStale(keep);
        return result.Select(a => paths.TryGetValue(a.Id, out var p) ? a with { CoverPath = p } : a).ToList();
    }

    private string? FindCached(AppInfo app)
    {
        foreach (var ext in Extensions)
        {
            var path = Path.Combine(_dir, $"{app.Id}-{app.CoverVersion}{ext}");
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length is 0 or > MaxBytes) continue;
                using var file = info.OpenRead();
                var head = new byte[8];
                var n = file.Read(head, 0, head.Length);
                if (AdImageCache.KindOf(head.AsSpan(0, n)) == ext) return path;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* не читается: скачаем заново */ }
        }
        return null;
    }

    private async Task<string?> DownloadAsync(AppInfo app, CancellationToken ct)
    {
        var r = await _api.GetBytesAsync($"/api/agent/apps/{app.Id}/cover", MaxBytes, ct).ConfigureAwait(false);
        if (!r.Ok || r.Value is null) { _log?.Invoke($"Обложка игры {app.Id}: не скачалась ({r.Error})"); return null; }
        var ext = AdImageCache.KindOf(r.Value);
        if (ext is null) { _log?.Invoke($"Обложка игры {app.Id}: это не PNG и не JPEG, пропущена"); return null; }
        var hash = Convert.ToHexString(SHA256.HashData(r.Value)).ToLowerInvariant();
        if (!hash.StartsWith(app.CoverVersion!, StringComparison.Ordinal)) { _log?.Invoke($"Обложка игры {app.Id}: отпечаток не совпал, пропущена"); return null; }

        var path = Path.Combine(_dir, $"{app.Id}-{app.CoverVersion}{ext}");
        var temp = path + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, r.Value, ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);   // файл появляется целиком: оверлей не прочитает недописанный
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"Обложка игры {app.Id}: не записалась ({e.Message})");
            try { File.Delete(temp); } catch (Exception) { /* уберём при следующей очистке */ }
            return null;
        }
    }

    private void RemoveStale(HashSet<string> keep)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_dir))
            {
                var name = Path.GetFileName(file);
                var ours = name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                           || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
                if (!ours || keep.Contains(name)) continue;
                try { File.Delete(file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* занят: удалим в следующий раз */ }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* папку не перечислить: не страшно */ }
    }
}
