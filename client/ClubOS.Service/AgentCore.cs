using ClubOS.Shared;

namespace ClubOS.Service;

/// <summary>
/// Мозг службы. Единственный, кто знает ключ ПК (внутри ServerApi) и токен вошедшего на этом ПК гостя/сотрудника.
/// Оверлей получает только то, что вернёт этот класс.
///
/// Политика при потере связи:
///  • пока связи ни разу не было — ПК закрыт (после загрузки ПК всегда стартует заблокированным);
///  • гость в игре: играет дальше, пока не вышел запас OfflineGraceSeconds или локальный счётчик времени пакета;
///  • сотрудник за рабочим столом: стол остаётся (его вход подтвердил сервер);
///  • закрытый ПК остаётся закрытым.
/// </summary>
public sealed class AgentCore
{
    private readonly ServerApi _api;
    private readonly AgentConfig _cfg;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _actionLock = new(1, 1);
    private readonly SemaphoreSlim _adsLock = new(1, 1);   // у рекламы своя очередь: скачивание картинок не должно задерживать вход гостя
    private static readonly TimeSpan AdsReportFresh = TimeSpan.FromSeconds(15);   // сообщению оверлея о заставке верим столько; не обновляется, значит оверлея нет
    private bool _adsOnScreen;
    private DateTimeOffset? _adsReportedAt;
    private readonly AdImageCache _ads;
    private readonly CoverCache _covers;
    private readonly SemaphoreSlim _catalogLock = new(1, 1);   // список игр тоже мимо общей очереди: скачивание обложек не должно задерживать вход гостя

    private ServerState? _last;
    private DateTimeOffset _lastOkAt;
    private bool _connected;
    private string? _lastError;
    private readonly ITokenStore _store;
    private string? _token; // токен входа на этом ПК; наружу не отдаётся никогда

    public AgentCore(ServerApi api, AgentConfig cfg, TimeProvider? time = null, ITokenStore? store = null, AdImageCache? ads = null, CoverCache? covers = null)
    {
        _api = api;
        _cfg = cfg;
        _ads = ads ?? new AdImageCache(api, AgentConfig.AdsDir);
        _covers = covers ?? new CoverCache(api, AgentConfig.CoversDir);
        _time = time ?? TimeProvider.System;
        _store = store ?? new MemoryTokenStore();
        _token = _store.Load(); // вход гостя переживает перезапуск службы; устаревший токен сервер сам отвергнет
    }

    private bool _programsRequested;

    /// <summary>Сервер просит заново собрать список установленных программ (сотрудник нажал «Обновить список»).</summary>
    public bool ProgramsRequested { get { lock (_gate) return _programsRequested; } }

    /// <summary>Список отправлен: просьба выполнена, до следующего ответа сервера её не повторяем.</summary>
    public void ProgramsReported() { lock (_gate) _programsRequested = false; }

    // ---------- состояние ----------
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var r = await _api.GetAsync<ServerState>(StatePath(), null, ct).ConfigureAwait(false);
        lock (_gate)
        {
            if (r.Ok && r.Value is not null)
            {
                _last = r.Value;
                _lastOkAt = _time.GetUtcNow();
                _connected = true;
                _lastError = null;
                _programsRequested = r.Value.ScanPrograms == true;   // администратор просил обновить список программ
                if (_token is not null && r.Value.Account is null) ForgetToken(); // сервер завершил вход (бездействие, блокировка)
            }
            else
            {
                _connected = false;
                _lastError = r.Status == 401 ? "Ключ этого ПК недействителен. Обратитесь к администратору" : r.Error;
            }
        }
    }

    /// <summary>
    /// Адрес опроса сервера. Пока оверлей сообщает, на экране заставка с баннером или нет, сервер получает это в параметре ads и считает время показа
    /// только пока заставка видна. Оверлей давно молчит (закрыт, завис): значит, баннера на экране нет. Оверлей вообще не сообщал (старая версия):
    /// параметра нет, сервер считает как раньше.
    /// </summary>
    private string StatePath()
    {
        lock (_gate)
        {
            if (_adsReportedAt is null) return "/api/agent/state";
            var visible = _adsOnScreen && _time.GetUtcNow() - _adsReportedAt.Value <= AdsReportFresh;
            return visible ? "/api/agent/state?ads=1" : "/api/agent/state?ads=0";
        }
    }

    private IpcResponse AdsVisible(IpcRequest req)
    {
        lock (_gate)
        {
            _adsOnScreen = req.Text == "1";
            _adsReportedAt = _time.GetUtcNow();
        }
        return new(req.Id, true, 200, null, Snapshot());
    }

    public AgentSnapshot Snapshot()
    {
        lock (_gate)
        {
            if (_last is null) return AgentSnapshot.Booting with { Message = _lastError ?? "Подключение к серверу…" };
            var elapsed = (int)Math.Max((_time.GetUtcNow() - _lastOkAt).TotalSeconds, 0);
            int? left = _last.Session?.SecondsLeft is int s ? Math.Max(s - elapsed, 0) : null;
            // Сервер считает гостя вошедшим, а у нас токена нет (служба потеряла его): кабинетом всё равно не воспользоваться,
            // поэтому показываем обычный экран входа — гость войдёт заново (сервер это разрешает).
            var account = _last.Mode == PcMode.Locked && _token is null && _last.Account?.Role == "guest" ? null : _last.Account;
            if (_connected) return new(true, _last.Mode, account, _last.Session, _last.Message, left, _last.ClubName, _last.PcName, _last.Notices);

            var message = _lastError ?? "Нет связи с сервером";
            return _last.Mode switch
            {
                PcMode.StaffDesktop => new(false, PcMode.StaffDesktop, _last.Account, null, message, null, _last.ClubName, _last.PcName),
                PcMode.GuestSession when elapsed <= _cfg.OfflineGraceSeconds && (left is null || left > 0)
                    => new(false, PcMode.GuestSession, _last.Account, _last.Session, message, left, _last.ClubName, _last.PcName),
                PcMode.GuestSession => new(false, PcMode.Locked, null, null, message, null, _last.ClubName, _last.PcName),
                _ => new(false, PcMode.Locked, account, null, message, null, _last.ClubName, _last.PcName),
            };
        }
    }

    // ---------- операции оверлея ----------
    public async Task<IpcResponse> HandleAsync(IpcRequest req, CancellationToken ct = default)
    {
        if (!Ops.All.Contains(req.Op)) return IpcResponse.Fail(req.Id, 400, "Неизвестная операция", Snapshot());
        if (Oversized(req)) return IpcResponse.Fail(req.Id, 400, "Слишком длинное значение", Snapshot());

        if (req.Op == Ops.State) return new(req.Id, true, 200, null, Snapshot());
        if (req.Op == Ops.AdsVisible) return AdsVisible(req);
        if (req.Op == Ops.Ads) return await AdsAsync(req, ct);   // мимо общей очереди действий, см. _adsLock
        if (req.Op == Ops.Apps) return await AppsAsync(req, ct); // и список игр: см. _catalogLock

        await _actionLock.WaitAsync(ct).ConfigureAwait(false); // действия идут по одному: нет гонок вроде «вход» + «выход»
        try
        {
            return req.Op switch
            {
                Ops.Tariffs => await TariffsAsync(req, ct),
                Ops.Products => await ProductsAsync(req, ct),
                Ops.Orders => await OrdersAsync(req, ct),
                Ops.OrderCreate => await PlainPostAsync(req, "/api/me/orders",
                    new { items = (req.Items ?? []).Select(i => new { product_id = i.ProductId, qty = i.Qty }), comment = req.Text }, useToken: true, ct),
                Ops.OrderCancel => await PlainPostAsync(req, $"/api/me/orders/{req.OrderId}/cancel", null, useToken: true, ct),
                Ops.CallAdmin => await CallAdminAsync(req, ct),
                Ops.CallState => await CallStateAsync(req, ct),
                Ops.Transactions => await TransactionsAsync(req, ct),
                Ops.RegisterRequestCode => await RequestCodeAsync(req, "/api/auth/register/request-code", ct),
                Ops.ResetRequestCode => await RequestCodeAsync(req, "/api/auth/password-reset/request-code", ct),
                Ops.RegisterConfirm => await LoginLikeAsync(req, "/api/auth/register/confirm",
                    new { phone = req.Phone, code = req.Code, password = req.Password, accept_terms = req.AcceptTerms }, ct),
                Ops.Login => await LoginLikeAsync(req, "/api/auth/login", new { phone = req.Phone, password = req.Password }, ct),
                Ops.ResetConfirm => await PlainPostAsync(req, "/api/auth/password-reset/confirm",
                    new { phone = req.Phone, code = req.Code, new_password = req.Password }, useToken: false, ct),
                Ops.Logout => await LogoutAsync(req, ct),
                Ops.KeepAlive => await KeepAliveAsync(req, ct),
                Ops.StartSession => await PlainPostAsync(req, "/api/me/sessions", new { tariff_id = req.TariffId }, useToken: true, ct),
                Ops.StopSession => await PlainPostAsync(req, "/api/me/sessions/stop", null, useToken: true, ct),
                Ops.SessionExtend => await PlainPostAsync(req, "/api/me/sessions/extend", new { tariff_id = req.TariffId }, useToken: true, ct),
                _ => IpcResponse.Fail(req.Id, 400, "Неизвестная операция", Snapshot()),
            };
        }
        finally { _actionLock.Release(); }
    }

    private static bool Oversized(IpcRequest r) =>
        (r.Phone?.Length ?? 0) > 64 || (r.Password?.Length ?? 0) > 256 || (r.Code?.Length ?? 0) > 16
        || (r.Text?.Length ?? 0) > 200 || (r.Items?.Count ?? 0) > 20 || r.Items?.Any(i => i.Qty is < 1 or > 20) == true;

    private string? Token { get { lock (_gate) return _token; } }
    private void SetToken(string? t) { lock (_gate) { _token = t; _store.Save(t); } }
    private void ForgetToken() { _token = null; _store.Save(null); }   // вызывается под _gate

    private async Task<IpcResponse> TariffsAsync(IpcRequest req, CancellationToken ct)
    {
        var r = await _api.GetAsync<List<TariffInfo>>("/api/agent/tariffs", null, ct).ConfigureAwait(false);
        if (!r.Ok) return await FailAsync(req, r.Status, r.Error, ct);
        return new(req.Id, true, 200, null, Snapshot(), Tariffs: r.Value);
    }

    private async Task<IpcResponse> AppsAsync(IpcRequest req, CancellationToken ct)
    {
        await _catalogLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var r = await _api.GetAsync<List<AppInfo>>("/api/agent/apps", null, ct).ConfigureAwait(false);
            if (!r.Ok) return await FailAsync(req, r.Status, r.Error, ct);
            var withCovers = await _covers.SyncAsync(r.Value ?? [], ct).ConfigureAwait(false);   // CoverPath: обложки, уже лежащие на ПК
            return new(req.Id, true, 200, null, Snapshot(), Apps: withCovers);
        }
        finally { _catalogLock.Release(); }
    }

    /// <summary>
    /// Баннеры для экрана входа: список с сервера плюс скачанные картинки (оверлей получает пути к файлам). Нужен только ключ ПК:
    /// экран блокировки виден до входа гостя. Сбой рекламы не должен разлогинивать гостя, поэтому FailAsync здесь не используется.
    /// </summary>
    private async Task<IpcResponse> AdsAsync(IpcRequest req, CancellationToken ct)
    {
        await _adsLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var r = await _api.GetAsync<List<AdInfo>>("/api/agent/ads", null, ct).ConfigureAwait(false);
            if (!r.Ok) return IpcResponse.Fail(req.Id, r.Status, r.Error ?? "Не удалось получить рекламу", Snapshot());
            var ready = await _ads.SyncAsync(r.Value ?? [], ct).ConfigureAwait(false);
            return new(req.Id, true, 200, null, Snapshot(), Ads: ready);
        }
        finally { _adsLock.Release(); }
    }

    private async Task<IpcResponse> ProductsAsync(IpcRequest req, CancellationToken ct)
    {
        var r = await _api.GetAsync<List<ProductInfo>>("/api/me/products", Token, ct).ConfigureAwait(false);
        if (!r.Ok) return await FailAsync(req, r.Status, r.Error, ct);
        return new(req.Id, true, 200, null, Snapshot(), Products: r.Value);
    }

    private async Task<IpcResponse> OrdersAsync(IpcRequest req, CancellationToken ct)
    {
        var r = await _api.GetAsync<List<OrderInfo>>("/api/me/orders", Token, ct).ConfigureAwait(false);
        if (!r.Ok) return await FailAsync(req, r.Status, r.Error, ct);
        return new(req.Id, true, 200, null, Snapshot(), Orders: r.Value);
    }

    private async Task<IpcResponse> CallAdminAsync(IpcRequest req, CancellationToken ct)
    {
        var r = await _api.PostAsync<CallInfo>("/api/me/calls", new { text = req.Text }, Token, ct).ConfigureAwait(false);
        if (!r.Ok) return await FailAsync(req, r.Status, r.Error, ct);
        return new(req.Id, true, 200, null, Snapshot(), OpenCall: r.Value);
    }

    private async Task<IpcResponse> CallStateAsync(IpcRequest req, CancellationToken ct)
    {
        var r = await _api.GetAsync<CurrentCallDto>("/api/me/calls/current", Token, ct).ConfigureAwait(false);
        if (!r.Ok) return await FailAsync(req, r.Status, r.Error, ct);
        return new(req.Id, true, 200, null, Snapshot(), OpenCall: r.Value?.Open);
    }

    private async Task<IpcResponse> TransactionsAsync(IpcRequest req, CancellationToken ct)
    {
        var r = await _api.GetAsync<List<TransactionInfo>>("/api/me/transactions", Token, ct).ConfigureAwait(false);
        if (!r.Ok) return await FailAsync(req, r.Status, r.Error, ct);
        return new(req.Id, true, 200, null, Snapshot(), Transactions: r.Value);
    }

    private async Task<IpcResponse> RequestCodeAsync(IpcRequest req, string path, CancellationToken ct)
    {
        var r = await _api.PostAsync<ResendDto>(path, new { phone = req.Phone }, null, ct).ConfigureAwait(false);
        return r.Ok ? new(req.Id, true, 200, null, Snapshot(), ResendAfter: r.Value?.ResendAfter)
                    : IpcResponse.Fail(req.Id, r.Status, r.Error ?? "Ошибка", Snapshot());
    }

    /// <summary>Вход и регистрация: сервер отдаёт токен, мы прячем его у себя и возвращаем оверлею только новое состояние.</summary>
    private async Task<IpcResponse> LoginLikeAsync(IpcRequest req, string path, object body, CancellationToken ct)
    {
        var r = await _api.PostAsync<LoginDto>(path, body, null, ct).ConfigureAwait(false);
        if (!r.Ok || r.Value is null) return IpcResponse.Fail(req.Id, r.Status, r.Error ?? "Ошибка входа", Snapshot());
        SetToken(r.Value.Token);
        await RefreshAsync(ct).ConfigureAwait(false);
        return new(req.Id, true, 200, null, Snapshot());
    }

    private async Task<IpcResponse> PlainPostAsync(IpcRequest req, string path, object? body, bool useToken, CancellationToken ct)
    {
        var r = await _api.PostAsync<System.Text.Json.JsonElement>(path, body, useToken ? Token : null, ct).ConfigureAwait(false);
        if (!r.Ok) return await FailAsync(req, r.Status, r.Error, ct);
        await RefreshAsync(ct).ConfigureAwait(false);
        return new(req.Id, true, 200, null, Snapshot());
    }

    private async Task<IpcResponse> LogoutAsync(IpcRequest req, CancellationToken ct)
    {
        if (Token is not null)
        {
            var r = await _api.PostAsync<System.Text.Json.JsonElement>("/api/auth/logout", null, Token, ct).ConfigureAwait(false);
            if (r.Status == 409) return IpcResponse.Fail(req.Id, 409, r.Error ?? "Сначала завершите сессию", Snapshot());
            if (!r.Ok && r.NetworkError) return IpcResponse.Fail(req.Id, 0, "Нет связи с сервером", Snapshot());
            SetToken(null); // выход удался или вход уже был закрыт сервером
        }
        await RefreshAsync(ct).ConfigureAwait(false);
        return new(req.Id, true, 200, null, Snapshot());
    }

    /// <summary>Пока гость что-то делает в кабинете, продлеваем его вход (иначе сервер выйдет по бездействию).</summary>
    private async Task<IpcResponse> KeepAliveAsync(IpcRequest req, CancellationToken ct)
    {
        if (Token is null || Snapshot().Account?.Role != "guest") return new(req.Id, true, 200, null, Snapshot());
        var r = await _api.GetAsync<AccountInfo>("/api/me", Token, ct).ConfigureAwait(false);
        if (r.Status == 401) { SetToken(null); await RefreshAsync(ct).ConfigureAwait(false); }
        return new(req.Id, true, 200, null, Snapshot());
    }

    /// <summary>Ошибка сервера. Если он сказал «вход недействителен» — забываем токен, оверлей вернётся на экран входа.</summary>
    private async Task<IpcResponse> FailAsync(IpcRequest req, int status, string? error, CancellationToken ct)
    {
        if (status == 401) { SetToken(null); await RefreshAsync(ct).ConfigureAwait(false); }
        return IpcResponse.Fail(req.Id, status, error ?? "Ошибка", Snapshot());
    }

    private sealed record ResendDto(int ResendAfter);
    private sealed record CurrentCallDto(CallInfo? Open);
    private sealed record LoginDto(string Token, AccountInfo? Account, string? Mode);
}
