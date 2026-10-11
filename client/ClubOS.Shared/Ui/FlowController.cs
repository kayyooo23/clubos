using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ClubOS.Shared.Ui;

public enum Screen { Login, RegisterPhone, RegisterConfirm, ResetPhone, ResetConfirm, Cabinet }

/// <summary>
/// Что показывать гостю и что происходит по его кнопкам. Не знает про WPF: окна только привязываются к свойствам
/// и вызывают методы. Благодаря этому вся логика экранов проверяется обычными тестами.
/// </summary>
public sealed class FlowController : INotifyPropertyChanged
{
    private static readonly TimeSpan KeepAliveEvery = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan TariffsRefreshEvery = TimeSpan.FromSeconds(60);

    private readonly IAgentClient _client;
    private readonly TimeProvider _time;
    private int _nextId;

    private Screen _screen = Screen.Login;
    private string _phone = "", _password = "", _password2 = "", _code = "";
    private bool _acceptTerms, _busy;
    private string? _error, _info;
    private AgentSnapshot _snapshot = AgentSnapshot.Booting;
    private IReadOnlyList<TariffInfo> _rawTariffs = [];
    private IReadOnlyList<TariffView> _tariffs = [], _extendTariffs = [];
    private DateTimeOffset _resendAt, _tariffsAt, _lastKeepAlive = DateTimeOffset.MinValue;
    private int _resendSecondsLeft;
    private bool _touched, _needTariffs;

    public FlowController(IAgentClient client, TimeProvider? time = null)
    {
        _client = client;
        _time = time ?? TimeProvider.System;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>Приходит при каждом новом состоянии от службы; окна по нему показываются и прячутся.</summary>
    public event Action<AgentSnapshot>? SnapshotChanged;

    // ---------- что показывать ----------
    public Screen Screen { get => _screen; private set => Set(ref _screen, value); }
    public string Phone { get => _phone; set => Set(ref _phone, value); }
    public string Password { get => _password; set => Set(ref _password, value); }
    public string Password2 { get => _password2; set => Set(ref _password2, value); }
    public string Code { get => _code; set => Set(ref _code, value); }
    public bool AcceptTerms { get => _acceptTerms; set => Set(ref _acceptTerms, value); }
    public string? Error { get => _error; private set => Set(ref _error, value); }
    public string? Info { get => _info; private set => Set(ref _info, value); }
    public bool Busy { get => _busy; private set { if (Set(ref _busy, value)) OnChanged(nameof(CanInteract)); } }
    public AgentSnapshot Snapshot => _snapshot;
    public IReadOnlyList<TariffView> Tariffs { get => _tariffs; private set => Set(ref _tariffs, value); }
    /// <summary>Пакеты, которыми можно продлить идущую сессию (пока сессии нет, список пуст).</summary>
    public IReadOnlyList<TariffView> ExtendTariffs { get => _extendTariffs; private set { if (Set(ref _extendTariffs, value)) OnChanged(nameof(CanExtend)); } }
    /// <summary>Продлить можно сессию, начатую с пакета или ночи (у почасовой оплаченного времени нет: её продлевает пополнение), если в клубе есть пакеты.</summary>
    public bool CanExtend => HasSession && _snapshot.Session?.Kind != "hourly" && _extendTariffs.Count > 0;
    public int ResendSecondsLeft { get => _resendSecondsLeft; private set => Set(ref _resendSecondsLeft, value); }

    public bool CanInteract => !Busy;
    public bool IsOffline => !_snapshot.Connected;
    public string? Banner => _snapshot.Connected ? null : _snapshot.Message ?? "Нет связи с сервером";
    public int Balance => _snapshot.Account?.Balance ?? 0;
    public string CabinetPhone => _snapshot.Account?.PhoneMasked ?? "";
    public bool HasSession => _snapshot.Session is not null;
    public string SessionTitle => _snapshot.Session?.Tariff ?? "";
    public string SessionClock => _snapshot.SecondsLeft is int s ? TariffFormatter.FormatClock(s) : "по факту";
    /// <summary>Тариф (пакет или ночь) кончился, а гость играет дальше с почасовой оплатой с баланса.</summary>
    public bool IsOvertime => _snapshot.Session?.Overtime == true;
    public string SessionStatus => IsOvertime ? "Тариф закончился" : "Идёт сессия";
    /// <summary>Что показывает крупные часы: пока идёт оплаченный тариф, сколько его осталось; после него, на сколько хватит баланса.</summary>
    public string ClockLabel => IsOvertime ? "Хватит баланса на" : "Осталось";
    /// <summary>Пояснение под таймером: что будет после тарифа или что происходит сейчас. Пусто, если пояснять нечего.</summary>
    public string SessionNote => SessionWarnings.Note(_snapshot);

    private static readonly System.Globalization.CultureInfo Ru = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
    /// <summary>Крупный заголовок экрана блокировки: название клуба (или CLUB OS, пока сервер не ответил), заглавными.</summary>
    public string ClubTitle => string.IsNullOrWhiteSpace(_snapshot.ClubName) ? "Club OS" : _snapshot.ClubName!.Trim();
    /// <summary>Какой это ПК («PC-3»), чтобы гость и администратор понимали, о чём речь. Пусто, пока сервер не ответил.</summary>
    public string PcLabel => string.IsNullOrWhiteSpace(_snapshot.PcName) ? "" : _snapshot.PcName!.Trim();

    // ---------- опрос службы ----------
    /// <summary>Вызывается таймером раз в секунду: обновляет состояние, счётчики и (при активности гостя) продлевает вход.</summary>
    public async Task PollAsync(CancellationToken ct = default)
    {
        if (Busy) return; // пока выполняется действие гостя, его ответ уже приведёт экран в порядок
        ApplyResponse(await CallAsync(new(0, Ops.State), ct));
        UpdateResendCounter();
        await LoadTariffsIfNeededAsync(ct);
        await KeepAliveIfNeededAsync(ct);
    }

    /// <summary>Оверлей вызывает при любом вводе гостя (мышь, клавиатура): пока он в кабинете, вход не должен истечь.</summary>
    public void Touch() => _touched = true;

    private async Task KeepAliveIfNeededAsync(CancellationToken ct)
    {
        if (Screen != Screen.Cabinet || !_touched) return;
        var now = _time.GetUtcNow();
        if (now - _lastKeepAlive < KeepAliveEvery) return;
        _touched = false;
        _lastKeepAlive = now;
        ApplyResponse(await CallAsync(new(0, Ops.KeepAlive), ct));
    }

    private async Task LoadTariffsIfNeededAsync(CancellationToken ct)
    {
        if (Screen != Screen.Cabinet && !HasSession) return;   // во время игры тарифы тоже нужны: ими продлевают сессию
        var now = _time.GetUtcNow();
        if (!_needTariffs && now - _tariffsAt < TariffsRefreshEvery) return;
        _needTariffs = false;
        _tariffsAt = now;
        var r = await CallAsync(new(0, Ops.Tariffs), ct);
        if (r.Ok && r.Tariffs is not null)
        {
            _rawTariffs = r.Tariffs;
            RebuildTariffs();
        }
        else if (!r.Ok) _tariffsAt = DateTimeOffset.MinValue; // повторим при следующем опросе
    }

    // ---------- кнопки ----------
    public Task LoginAsync() => RunAsync(async () =>
    {
        if (Check(Validation.Phone(Phone)) || Check(Validation.LoginPassword(Password))) return;
        if (Handle(await CallAsync(new(0, Ops.Login, Phone: Phone, Password: Password)))) Password = "";
    });

    /// <summary>«Получить код» (и «Отправить ещё раз») для регистрации и для сброса пароля — по текущему экрану.</summary>
    public Task RequestCodeAsync() => RunAsync(async () =>
    {
        if (Check(Validation.Phone(Phone))) return;
        var registering = Screen is Screen.RegisterPhone or Screen.RegisterConfirm;
        var r = await CallAsync(new(0, registering ? Ops.RegisterRequestCode : Ops.ResetRequestCode, Phone: Phone));
        if (!Handle(r)) return;
        _resendAt = _time.GetUtcNow().AddSeconds(r.ResendAfter ?? 60);
        UpdateResendCounter();
        Screen = registering ? Screen.RegisterConfirm : Screen.ResetConfirm;
        Info = "Мы отправили код в SMS. Если номер уже зарегистрирован, SMS не придёт: войдите по паролю или сбросьте его.";
    });

    public Task ConfirmAsync() => RunAsync(async () =>
    {
        if (Check(Validation.Phone(Phone)) || Check(Validation.Code(Code)) || Check(Validation.NewPassword(Password, Password2))) return;
        if (Screen == Screen.RegisterConfirm)
        {
            if (!AcceptTerms) { Error = "Нужно согласие на обработку персональных данных"; return; }
            if (Handle(await CallAsync(new(0, Ops.RegisterConfirm, Phone: Phone, Code: Code, Password: Password, AcceptTerms: true))))
                ClearSecrets();
        }
        else if (Screen == Screen.ResetConfirm)
        {
            if (!Handle(await CallAsync(new(0, Ops.ResetConfirm, Phone: Phone, Code: Code, Password: Password)))) return;
            ClearSecrets();
            Screen = Screen.Login;
            Info = "Пароль изменён. Войдите с новым паролем.";
        }
    });

    public Task StartAsync(int tariffId) => RunAsync(async () =>
        Handle(await CallAsync(new(0, Ops.StartSession, TariffId: tariffId))));

    public Task StopAsync() => RunAsync(async () => Handle(await CallAsync(new(0, Ops.StopSession))));

    /// <summary>Продлевает идущую сессию пакетом tariffId. true, если время добавлено (ошибка сервера остаётся в Error).</summary>
    public async Task<bool> ExtendAsync(int tariffId)
    {
        var ok = false;
        await RunAsync(async () => ok = Handle(await CallAsync(new(0, Ops.SessionExtend, TariffId: tariffId))));
        return ok;
    }

    public Task LogoutAsync() => RunAsync(async () => Handle(await CallAsync(new(0, Ops.Logout))));

    public void GoTo(Screen screen)
    {
        ClearSecrets();
        Error = Info = null;
        Screen = screen;
    }

    // ---------- внутреннее ----------
    private Task<IpcResponse> CallAsync(IpcRequest request, CancellationToken ct = default) =>
        _client.SendAsync(request with { Id = Interlocked.Increment(ref _nextId) }, ct);

    private async Task RunAsync(Func<Task> action)
    {
        if (Busy) return;
        Busy = true;
        Error = Info = null;
        try { await action(); }
        finally { Busy = false; }
    }

    /// <summary>Показывает ошибку и возвращает true, если проверка не пройдена.</summary>
    private bool Check(string? problem)
    {
        if (problem is null) return false;
        Error = problem;
        return true;
    }

    /// <summary>Применяет ответ службы; при ошибке показывает её гостю. Возвращает true, если всё хорошо.</summary>
    private bool Handle(IpcResponse r)
    {
        ApplyResponse(r);
        if (r.Ok) return true;
        Error = r.Error ?? "Что-то пошло не так";
        return false;
    }

    private void ApplyResponse(IpcResponse r)
    {
        if (r.Snapshot is not null) Apply(r.Snapshot);
        else if (!r.Ok) Apply(AgentSnapshot.Booting with { Message = r.Error ?? "Служба ClubOS недоступна" });
    }

    private void Apply(AgentSnapshot s)
    {
        _snapshot = s;
        foreach (var name in new[] { nameof(Snapshot), nameof(IsOffline), nameof(Banner), nameof(Balance), nameof(CabinetPhone),
                                     nameof(HasSession), nameof(SessionTitle), nameof(SessionClock), nameof(ClubTitle), nameof(PcLabel),
                                     nameof(IsOvertime), nameof(SessionStatus), nameof(ClockLabel), nameof(SessionNote), nameof(CanExtend) })
            OnChanged(name);
        RouteScreen(s);
        RebuildTariffs();
        SnapshotChanged?.Invoke(s);
    }

    private void RouteScreen(AgentSnapshot s)
    {
        if (s.Mode == PcMode.Locked && s.Account is { Role: "guest" })
        {
            if (Screen != Screen.Cabinet)
            {
                Screen = Screen.Cabinet;
                ClearSecrets();
                _needTariffs = true;
            }
        }
        else if (Screen == Screen.Cabinet || s.Mode != PcMode.Locked)
        {
            // вышли из кабинета (сами, по бездействию, блокировка) или ПК разблокирован/занят сотрудником: начинаем с чистого входа
            ClearSecrets();
            Screen = Screen.Login;
        }
    }

    /// <summary>Список заменяем, только если он правда изменился: иначе окно пересоздавало бы кнопки тарифов каждую секунду
    /// (опрос идёт раз в секунду), и нажатие мышью могло «потеряться», пока кнопку подменяют.</summary>
    private void RebuildTariffs()
    {
        var fresh = _rawTariffs.Select(t => TariffFormatter.ToView(t, Balance)).ToList();
        if (!fresh.SequenceEqual(_tariffs)) Tariffs = fresh;
        var extend = _rawTariffs.Where(t => t.Kind == "package").Select(t => TariffFormatter.ToExtendView(t, Balance)).ToList();
        if (!extend.SequenceEqual(_extendTariffs)) ExtendTariffs = extend;
    }

    private void ClearSecrets()
    {
        Password = Password2 = Code = "";
        AcceptTerms = false;
    }

    private void UpdateResendCounter() =>
        ResendSecondsLeft = Math.Max((int)Math.Ceiling((_resendAt - _time.GetUtcNow()).TotalSeconds), 0);

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnChanged(name);
        return true;
    }

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
