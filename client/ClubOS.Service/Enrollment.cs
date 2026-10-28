using ClubOS.Shared;

namespace ClubOS.Service;

/// <summary>
/// Откуда у службы ключ ПК и что делать, если сервер его не принимает.
///  1. Ключ вписан в настройки (PcKey / CLUBOS_PC_KEY): он главный, как раньше.
///  2. Иначе ключ, сохранённый после прошлого подключения по коду.
///  3. Иначе служба сама подключается по коду из настроек (POST /api/agent/enroll), сохраняет полученный ключ и работает дальше.
/// Сервер отверг сохранённый ключ (401: ПК удалён или ключ сменён; 409 machine_mismatch: диск скопирован на другой компьютер) — ключ забывается
/// и служба подключается по коду заново. Тот же компьютер получит свой ПК с новым ключом, скопированный образ станет отдельным ПК.
/// Неудачи не заваливают сервер повторами: при проблеме с самим кодом следующая попытка через десять минут (иначе тридцать ПК за одним
/// адресом исчерпали бы лимит неверных попыток), при обрыве связи чаще.
/// </summary>
public sealed class EnrollmentManager
{
    public static readonly TimeSpan CodeRetry = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan[] NetworkBackoff = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];

    private enum Source { None, Explicit, Stored, Enrolled }

    private readonly ServerApi _api;
    private readonly AgentConfig _cfg;
    private readonly IIdentityStore _store;
    private readonly string _machineId;
    private readonly string _hostname;
    private readonly TimeProvider _time;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Source _source;
    private DateTimeOffset _nextTry;
    private int _networkFailures;

    public EnrollmentManager(ServerApi api, AgentConfig cfg, IIdentityStore store, string machineId, string hostname, TimeProvider? time = null, Action<string>? log = null)
    {
        _api = api;
        _cfg = cfg;
        _store = store;
        _machineId = machineId;
        _hostname = hostname;
        _time = time ?? TimeProvider.System;
        _log = log;
    }

    /// <summary>Что показать на экране блокировки, пока ПК не подключён (null: проблем нет).</summary>
    public string? Problem { get; private set; }

    public PcIdentity? Identity { get; private set; }

    /// <summary>Есть ли у службы рабочий ключ. Если нет, пробует получить (не чаще, чем разрешают паузы после неудач).</summary>
    public async Task<bool> EnsureAsync(CancellationToken ct = default)
    {
        if (_api.HasKey) return true;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_api.HasKey) return true;
            if (!string.IsNullOrWhiteSpace(_cfg.PcKey))
            {
                _api.SetKey(_cfg.PcKey);
                _source = Source.Explicit;
                Problem = null;
                return true;
            }
            if (_store.Load() is { } saved)
            {
                _api.SetKey(saved.Key);
                Identity = saved;
                _source = Source.Stored;
                Problem = null;
                return true;
            }
            if (string.IsNullOrWhiteSpace(_cfg.EnrollCode))
            {
                Problem = "ПК не подключён к клубу: в настройках службы нет ни ключа, ни кода подключения";
                return false;
            }
            if (_time.GetUtcNow() < _nextTry) return false;
            return await EnrollAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<bool> EnrollAsync(CancellationToken ct)
    {
        var r = await _api.PostAsync<EnrollDto>("/api/agent/enroll", new { code = _cfg.EnrollCode, machine_id = _machineId, hostname = _hostname }, null, ct).ConfigureAwait(false);
        if (r.Ok && r.Value is { Key.Length: > 0 } v)
        {
            Identity = new PcIdentity(v.Key, v.PcId, v.PcName, v.ClubName);
            _store.Save(Identity);
            _api.SetKey(v.Key);
            _source = Source.Enrolled;
            _networkFailures = 0;
            Problem = null;
            _log?.Invoke($"ПК подключён к клубу «{v.ClubName}» как «{v.PcName}»");
            return true;
        }
        if (r.NetworkError)
        {
            _nextTry = _time.GetUtcNow() + NetworkBackoff[Math.Min(_networkFailures++, NetworkBackoff.Length - 1)];
            Problem = "Нет связи с сервером";
        }
        else
        {
            _nextTry = _time.GetUtcNow() + CodeRetry;                 // код неверный, истёк или исчерпан, либо сервер ответил ошибкой: частые повторы ничего не изменят
            Problem = r.Status is 403 or 422 or 429 ? $"{r.Error}. Обратитесь к администратору клуба" : r.Error ?? "Не удалось подключить ПК";
            _log?.Invoke($"Подключение по коду не удалось ({r.Status}): {r.Error}");
        }
        return false;
    }

    /// <summary>
    /// Сервер ответил на запрос с ключом отказом. Возвращает текст для экрана блокировки, если отказ связан с ключом или привязкой к железу (иначе null).
    /// Если ключ получен подключением по коду, он забывается, и следующий проход подключит ПК заново.
    /// </summary>
    public string? KeyRejected(int status, string? code)
    {
        var mismatch = status == 409 && code == "machine_mismatch";
        if (status != 401 && !mismatch) return null;
        if (_source is Source.Stored or Source.Enrolled && !string.IsNullOrWhiteSpace(_cfg.EnrollCode))
        {
            _store.Save(null);
            _api.SetKey(null);
            Identity = null;
            _source = Source.None;
            _nextTry = default;
            _log?.Invoke(mismatch ? "Ключ ПК привязан к другому компьютеру (копия диска): подключаемся по коду как отдельный ПК" : "Ключ ПК отвергнут сервером: подключаемся по коду заново");
            return "Переподключение к клубу…";
        }
        return mismatch
            ? "Этот ключ ПК привязан к другому компьютеру. Обратитесь к администратору"
            : "Ключ этого ПК недействителен. Обратитесь к администратору";
    }

    private sealed record EnrollDto(int PcId, string PcName, string ClubName, string? Zone, string Key);
}
