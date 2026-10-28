using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ClubOS.Shared;

namespace ClubOS.Service.Update;

/// <summary>
/// Готовит версию к установке: проверяет предложение сервера, скачивает пакет, сверяет с подписанным, распаковывает рядом с работающей версией.
/// Порядок проверок нарочно такой: подпись (до скачивания, чтобы чужие пакеты даже не качать), отпечаток и размер скачанного, безопасная распаковка,
/// содержимое (служба, оверлей, версия внутри). Адрес пакета не берётся из ответа сервера: он всегда один и тот же, по номеру версии.
/// Только после всего этого версия лежит в своей папке и может быть включена. Сам запуск новой версии делает UpdateApplier.
/// </summary>
public sealed partial class UpdateInstaller
{
    public const long MaxPackageBytes = 400L * 1024 * 1024;

    /// <summary>Permanent: версия негодная (подпись, содержимое): её не пробуем снова несколько часов. Не permanent: сбой связи, диска, места, можно пробовать дальше.</summary>
    public sealed record Result(string? Dir, string? Error, bool Permanent)
    {
        public bool Ok => Dir is not null;
    }

    private readonly ServerApi _api;
    private readonly System.Security.Cryptography.ECDsa? _key;
    private readonly InstallLayout _layout;
    private readonly string _downloads;
    private readonly BadVersions _bad;
    private readonly PackageValidator _validator;
    private readonly Func<string, long> _freeSpace;
    private readonly Action<string>? _log;

    public UpdateInstaller(ServerApi api, System.Security.Cryptography.ECDsa? publicKey, InstallLayout layout, string downloadsDir, BadVersions bad,
        PackageValidator? validator = null, Func<string, long>? freeSpace = null, Action<string>? log = null)
    {
        _api = api;
        _key = publicKey;
        _layout = layout;
        _downloads = downloadsDir;
        _bad = bad;
        _validator = validator ?? new PackageValidator();
        _freeSpace = freeSpace ?? (path => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace);
        _log = log;
    }

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Pattern();

    public async Task<Result> PrepareAsync(UpdateOffer offer, string currentVersion, CancellationToken ct)
    {
        if (_key is null) return Fail("Автообновление выключено: в программу не вшит ключ подписи", false);
        if (!AppVersion.IsValid(offer.Version) || !AppVersion.IsNewer(offer.Version, currentVersion)) return Fail("Предложенная версия не новее установленной", false);
        if (_bad.IsBad(offer.Version)) return Fail($"Версия {offer.Version} недавно не заработала, пока не предлагаем её снова", false);
        if (offer.Size is <= 0 or > MaxPackageBytes || !Sha256Pattern().IsMatch(offer.Sha256 ?? "")) return Fail("Сервер прислал неверные сведения о пакете", true, offer.Version);
        if (!ReleaseSigning.Verify(_key, offer.Version, offer.Sha256!, offer.Size, offer.Signature))
            return Fail("Подпись версии не сошлась: пакет не принят", true, offer.Version);     // самое важное: чужую версию не ставим

        var finalDir = _layout.DirFor(offer.Version);
        try
        {
            if (Directory.Exists(finalDir))
            {
                if (_validator.Validate(finalDir, offer.Version) is null) return new Result(finalDir, null, false);   // уже готова (ждала, пока ПК освободится)
                Directory.Delete(finalDir, true);
            }
            Directory.CreateDirectory(_downloads);
            Directory.CreateDirectory(_layout.VersionsRoot);
            if (_freeSpace(_downloads) < offer.Size * 2 || _freeSpace(_layout.VersionsRoot) < offer.Size * 4)
                return Fail("Не хватает места на диске для обновления", false);

            var zip = Path.Combine(_downloads, $"{offer.Version}.zip");
            var download = await _api.DownloadToFileAsync($"/api/agent/update/package/{offer.Id}", zip, offer.Size, 60, ct).ConfigureAwait(false);
            if (!download.Ok) return Fail(download.Error ?? "Не удалось скачать пакет", false);
            if (download.Value.Size != offer.Size || !download.Value.Sha256.Equals(offer.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(zip);
                return Fail("Скачанный пакет не совпал с подписанным (размер или sha256)", true, offer.Version);
            }

            var temp = finalDir + ".tmp";
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
            var problem = SafeUnzip.Extract(zip, temp) ?? _validator.Validate(temp, offer.Version);
            File.Delete(zip);
            if (problem is not null)
            {
                if (Directory.Exists(temp)) Directory.Delete(temp, true);
                return Fail(problem, true, offer.Version);
            }
            Directory.Move(temp, finalDir);
            _log?.Invoke($"Версия {offer.Version} скачана, проверена и подготовлена");
            return new Result(finalDir, null, false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Fail($"Не удалось подготовить версию: {e.Message}", false);
        }
    }

    private Result Fail(string error, bool permanent, string? version = null)
    {
        if (permanent && version is not null) _bad.Add(version);
        _log?.Invoke(error);
        return new Result(null, error.Length <= 200 ? error : error[..200], permanent);
    }
}
