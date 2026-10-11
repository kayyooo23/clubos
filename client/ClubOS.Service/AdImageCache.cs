using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.RegularExpressions;
using ClubOS.Shared;

namespace ClubOS.Service;

/// <summary>
/// Рекламные баннеры на диске ПК. Оверлей не ходит на сервер сам (у него нет ключа ПК), поэтому картинки скачивает служба,
/// проверяет и кладёт в папку, откуда оверлей их только читает. Файл называется «номер-отпечаток.расширение»:
/// заменили картинку на сервере, отпечаток изменился, скачается новая; прежняя удалится.
/// Принимаем только PNG и JPEG (тип определяем по первым байтам, а не по тому, что назвал сервер), не больше 2 МБ, и только если
/// SHA-256 скачанного совпал с отпечатком из списка: подмена или обрыв по дороге не дойдут до экрана.
/// </summary>
public sealed partial class AdImageCache
{
    public const int MaxBytes = 2 * 1024 * 1024, MaxAds = 12, MinSeconds = 3, MaxSeconds = 120, DefaultSeconds = 10;

    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly string[] Extensions = [".png", ".jpg"];
    private static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(6);   // служба отвечает оверлею не дольше 10 секунд

    private readonly ServerApi _api;
    private readonly string _dir;
    private readonly TimeSpan _budget;
    private readonly Action<string>? _log;

    public AdImageCache(ServerApi api, string dir, TimeSpan? budget = null, Action<string>? log = null)
    {
        _api = api;
        _dir = dir;
        _budget = budget ?? DefaultBudget;
        _log = log;
    }

    public string Dir => _dir;

    [GeneratedRegex("^[0-9a-f]{8,64}$")]
    private static partial Regex VersionPattern();

    /// <summary>
    /// Приводит список с сервера к тому, что можно показывать: у баннеров, чьи картинки уже на диске, заполняется Path.
    /// Баннеры, которые не успели скачаться за отведённое время, возвращаются без Path (оверлей спросит ещё раз через несколько секунд).
    /// Лишние файлы из папки удаляются: выключили рекламу или убрали баннер, и картинка не лежит на ПК зря.
    /// </summary>
    public async Task<IReadOnlyList<AdInfo>> SyncAsync(IEnumerable<AdInfo> wanted, CancellationToken ct = default)
    {
        var list = wanted.Where(a => a.Id > 0 && a.Version is not null && VersionPattern().IsMatch(a.Version))
                         .Take(MaxAds).Select(Normalize).ToList();
        try { Directory.CreateDirectory(_dir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"Папка баннеров недоступна ({e.Message})");
            return list;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(_budget);
        var result = new List<AdInfo>();
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ad in list)
        {
            var path = FindCached(ad);
            if (path is null && !budget.IsCancellationRequested)
            {
                try { path = await DownloadAsync(ad, budget.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* вышло время: остальные подхватим в следующий раз */ }
            }
            ct.ThrowIfCancellationRequested();
            if (path is not null) keep.Add(Path.GetFileName(path));
            result.Add(path is null ? ad : ad with { Path = path });
        }
        RemoveStale(keep);
        return result;
    }

    private static AdInfo Normalize(AdInfo ad) =>
        ad with { Path = null, Seconds = ad.Seconds <= 0 ? DefaultSeconds : Math.Clamp(ad.Seconds, MinSeconds, MaxSeconds) };

    private string? FindCached(AdInfo ad)
    {
        foreach (var ext in Extensions)
        {
            var path = Path.Combine(_dir, $"{ad.Id}-{ad.Version}{ext}");
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length is 0 or > MaxBytes) continue;
                using var file = info.OpenRead();
                var head = new byte[8];
                var n = file.Read(head, 0, head.Length);
                if (KindOf(head.AsSpan(0, n)) == ext) return path;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* не читается: скачаем заново */ }
        }
        return null;
    }

    private async Task<string?> DownloadAsync(AdInfo ad, CancellationToken ct)
    {
        var r = await _api.GetBytesAsync($"/api/agent/ads/{ad.Id}/image", MaxBytes, ct).ConfigureAwait(false);
        if (!r.Ok || r.Value is null)
        {
            _log?.Invoke($"Баннер {ad.Id}: не скачался ({r.Error})");
            return null;
        }
        var ext = KindOf(r.Value);
        if (ext is null) { _log?.Invoke($"Баннер {ad.Id}: это не PNG и не JPEG, пропущен"); return null; }
        var hash = Convert.ToHexString(SHA256.HashData(r.Value)).ToLowerInvariant();
        if (!hash.StartsWith(ad.Version!, StringComparison.Ordinal)) { _log?.Invoke($"Баннер {ad.Id}: отпечаток не совпал, пропущен"); return null; }

        var path = Path.Combine(_dir, $"{ad.Id}-{ad.Version}{ext}");
        var temp = path + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, r.Value, ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);   // файл появляется целиком: оверлей не прочитает недописанный
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"Баннер {ad.Id}: не записался ({e.Message})");
            try { File.Delete(temp); } catch (Exception) { /* уберём при следующей очистке */ }
            return null;
        }
    }

    /// <summary>Расширение по первым байтам: PNG или JPEG. Иначе null.</summary>
    internal static string? KindOf(ReadOnlySpan<byte> data)
    {
        if (data.Length >= PngMagic.Length && data[..PngMagic.Length].SequenceEqual(PngMagic)) return ".png";
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return ".jpg";
        return null;
    }

    /// <summary>Удаляет только то, что кладём мы (картинки и недописанные временные файлы). Чужие файлы в папке не трогаем.</summary>
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

    /// <summary>
    /// Права на папку: система и администраторы пишут, остальные только читают (оверлей работает от имени гостя и не должен уметь подменить картинку).
    /// Применяется, только когда служба запущена от системы: при запуске из консоли под обычным пользователем папка остаётся как есть.
    /// </summary>
    public static void Protect(string dir, Action<string>? log = null)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var me = WindowsIdentity.GetCurrent();
            if (!me.IsSystem) return;
            var info = Directory.CreateDirectory(dir);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            const InheritanceFlags both = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, both, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, both, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute, both, PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(security);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            log?.Invoke($"Не удалось выставить права на папку баннеров: {e.Message}");
        }
    }
}
