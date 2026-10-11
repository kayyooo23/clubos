using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace ClubOS.Shared.Ui;

public enum LauncherPage { Library, Bar, Tariffs }

/// <summary>Карточка игры в меню, готовая к показу.</summary>
public sealed record AppCard(AppInfo Info, string Title, string Initial, string Category, string AgeText, bool Available, string? Hint, string? CoverPath = null)
{
    public int Id => Info.Id;
    /// <summary>Есть ли у игры обложка (файл скачан и лежит в папке обложек).</summary>
    public bool HasCover => CoverPath is not null;
}

/// <summary>Кнопка фильтра по категории. Key пустой у «Все».</summary>
public sealed record CategoryChip(string Key, string Title, bool Selected);

/// <summary>Как меню запускает игры на ПК. В тестах подменяется заглушкой.</summary>
public interface IAppLauncher
{
    bool IsAvailable(AppInfo app);

    /// <summary>Запускает игру. Возвращает null, если всё хорошо, иначе текст ошибки для гостя.</summary>
    string? Launch(AppInfo app);
}

/// <summary>
/// Меню гостя: вкладки «Библиотека» (сверху игры, ниже программы), «Бар», «Тарифы», поиск, категории игр, запуск. Не знает про WPF.
/// Работает поверх FlowController: тот отвечает за вход, баланс и сессию, а этот класс за каталог и запуск.
/// Меню видно, пока гость в кабинете (до начала сессии) или играет.
/// </summary>
public sealed class LauncherModel : INotifyPropertyChanged
{
    private static readonly TimeSpan AppsRefreshEvery = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AppsRetryAfter = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CoversRetryAfter = TimeSpan.FromSeconds(5);   // служба ещё качает обложки: спросим скоро
    private static readonly TimeSpan AvailabilityEvery = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ErrorLifetime = TimeSpan.FromSeconds(10);
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private readonly IAgentClient _client;
    private readonly IAppLauncher _launcher;
    private readonly string _coversDir;
    private readonly TimeProvider _time;
    private int _nextId;

    private IReadOnlyList<AppInfo> _apps = [];
    private bool _appsLoaded, _needApps = true, _inLauncher, _hadSession;
    private DateTimeOffset _appsAt = DateTimeOffset.MinValue, _availabilityAt, _errorAt, _noticeAt;
    private LauncherPage _page = LauncherPage.Tariffs;
    private string _search = "", _category = "";
    private string? _launchError, _notice;
    private IReadOnlyList<AppCard> _gameCards = [], _appCards = [];
    private IReadOnlyList<CategoryChip> _chips = [];
    private IReadOnlyList<ProductCard> _productCards = [];

    public LauncherModel(FlowController flow, IAgentClient client, IAppLauncher launcher, TimeProvider? time = null, string? adsDir = null, string? coversDir = null)
    {
        Flow = flow;
        _client = client;
        _launcher = launcher;
        _time = time ?? TimeProvider.System;
        _coversDir = coversDir ?? AgentConfig.CoversDir;
        Bar = new BarModel(flow, client, _time);
        Ads = new AdBannerModel(client, _time, adsDir);
        Attract = new AttractModel(flow, Ads, client, _time);
        Bar.Changed += () => { if (Page == LauncherPage.Bar) Rebuild(); };
        flow.SnapshotChanged += OnSnapshot;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>Игра успешно запущена: окно меню может свернуться, чтобы не закрывать её.</summary>
    public event Action<AppInfo>? AppLaunched;

    public FlowController Flow { get; }
    /// <summary>Бар (заказы еды и напитков) и вызов администратора.</summary>
    public BarModel Bar { get; }
    /// <summary>Рекламные баннеры партнёров (экран блокировки).</summary>
    public AdBannerModel Ads { get; }
    /// <summary>Заставка: пока ПК простаивает, баннер на весь экран; нажатие показывает форму входа.</summary>
    public AttractModel Attract { get; }

    // ---------- что показывать ----------
    public LauncherPage Page { get => _page; private set { if (Set(ref _page, value)) RaisePageProperties(); } }
    public bool IsLibraryPage { get => Page == LauncherPage.Library; set { if (value) SelectPage(LauncherPage.Library); } }
    public bool IsBarPage { get => Page == LauncherPage.Bar; set { if (value) SelectPage(LauncherPage.Bar); } }
    public bool IsTariffsPage { get => Page == LauncherPage.Tariffs; set { if (value) SelectPage(LauncherPage.Tariffs); } }
    public bool ShowCatalog => Page == LauncherPage.Library;
    public bool ShowBar => Page == LauncherPage.Bar;
    /// <summary>Поиск и фильтр по категориям нужны и в играх, и в баре.</summary>
    public bool ShowSearch => ShowCatalog || ShowBar;
    public string PageTitle => Page switch { LauncherPage.Library => "Библиотека", LauncherPage.Bar => "Бар", _ => TariffsTitle };

    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value ?? "")) Rebuild(); }
    }

    public IReadOnlyList<CategoryChip> Chips { get => _chips; private set => Set(ref _chips, value); }
    public IReadOnlyList<ProductCard> ProductCards { get => _productCards; private set { if (Set(ref _productCards, value)) OnChanged(nameof(BarEmptyText)); } }
    /// <summary>Игры (верхний раздел библиотеки): фильтруются и поиском, и категорией.</summary>
    public IReadOnlyList<AppCard> GameCards { get => _gameCards; private set { if (Set(ref _gameCards, value)) { OnChanged(nameof(EmptyText)); OnChanged(nameof(ShowGames)); } } }
    /// <summary>Программы (раздел ниже игр): фильтруются только поиском.</summary>
    public IReadOnlyList<AppCard> AppCards { get => _appCards; private set { if (Set(ref _appCards, value)) { OnChanged(nameof(EmptyText)); OnChanged(nameof(ShowApps)); } } }
    /// <summary>Раздел с играми виден, только если в нём есть что показать (поиск мог всё скрыть).</summary>
    public bool ShowGames => ShowCatalog && _gameCards.Count > 0;
    public bool ShowApps => ShowCatalog && _appCards.Count > 0;
    public string? LaunchError { get => _launchError; private set => Set(ref _launchError, value); }
    /// <summary>Короткое сообщение об успехе (например, «Время добавлено»); пропадает само.</summary>
    public string? Notice { get => _notice; private set => Set(ref _notice, value); }
    /// <summary>У почасовой сессии продлевать нечего: на странице тарифов остаётся пояснение вместо карточек.</summary>
    public bool ShowNoExtendHint => Flow.HasSession && !Flow.CanExtend;

    /// <summary>Играть можно, только пока идёт сессия.</summary>
    public bool CanLaunch => Flow.HasSession;
    /// <summary>Выйти из кабинета можно, пока сессия не началась (во время игры сервер выход не разрешит).</summary>
    public bool CanLogout => !Flow.HasSession;
    /// <summary>Кнопка «Продлить тариф»: идёт сессия с пакетом или ночью, и в клубе есть пакеты для продления.</summary>
    public bool CanExtend => Flow.CanExtend;
    /// <summary>«Продлить тариф» белой заливкой, кроме вкладки «Бар»: там главная кнопка «Заказать», и две красные заливки спорили бы.</summary>
    public bool ExtendIsPrimary => CanExtend && !ShowBar;
    public bool ExtendIsOutline => CanExtend && ShowBar;
    /// <summary>Вкладка тарифов нужна, пока сессии нет (выбрать тариф) или её можно продлить; у почасовой сессии её нет.</summary>
    public bool ShowTariffsTab => !Flow.HasSession || Flow.CanExtend;
    /// <summary>Во время игры вкладка тарифов называется «Продление»: на ней покупают дополнительное время, а не начинают сессию.</summary>
    public string TariffsTitle => Flow.HasSession ? "Продление" : "Тарифы";

    /// <summary>Что написать вместо пустого меню бара.</summary>
    public string? BarEmptyText
    {
        get
        {
            if (!ShowBar || _productCards.Count > 0) return null;
            if (!Bar.ProductsLoaded) return Bar.ProductsFailed ? "Не удалось загрузить меню. Пробуем ещё раз…" : "Загружаем меню…";
            return Bar.Build("", "").Cards.Count == 0 ? "В баре пока ничего нет" : "Ничего не найдено";
        }
    }

    /// <summary>Что написать вместо пустого списка. Null, если карточки есть.</summary>
    public string? EmptyText
    {
        get
        {
            if (!ShowCatalog || _gameCards.Count > 0 || _appCards.Count > 0) return null;
            if (!_appsLoaded) return "Загружаем список…";
            return _apps.Any(a => a.Tab is "games" or "apps") ? "Ничего не найдено" : "Список пока пуст. Администратор добавит игры в настройках клуба";
        }
    }

    // ---------- действия ----------
    public void SelectPage(LauncherPage page)
    {
        _search = "";
        _category = "";
        OnChanged(nameof(Search));
        LaunchError = null;
        Page = page;
        Rebuild();
    }

    public void SelectCategory(string key)
    {
        _category = key ?? "";
        Rebuild();
    }

    /// <summary>Гость нажал на карточку.</summary>
    public void Launch(AppCard card)
    {
        LaunchError = null;
        if (!Flow.HasSession)
        {
            SelectPage(LauncherPage.Tariffs);   // сначала тариф: без него ПК всё равно закрыт
            ShowError("Чтобы запускать игры, выберите тариф и начните сессию");
            return;
        }
        if (!card.Available)
        {
            ShowError($"«{card.Title}» не установлена на этом ПК");
            return;
        }
        var error = _launcher.Launch(card.Info);
        if (error is not null) { ShowError(error); return; }
        AppLaunched?.Invoke(card.Info);
    }

    /// <summary>Вызывается таймером раз в секунду, после FlowController.PollAsync.</summary>
    public async Task PollAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        if (LaunchError is not null && now - _errorAt >= ErrorLifetime) LaunchError = null;
        if (Notice is not null && now - _noticeAt >= ErrorLifetime) Notice = null;
        if (Flow.Screen != Screen.Cabinet && !Flow.HasSession) await Ads.PollAsync(ct);   // баннеры нужны только на экране входа: в кабинете и в игре их не показываем и не скачиваем
        await Attract.PollAsync(ct);                                                      // заставка решает, видна ли она, и сообщает об этом службе (в кабинете: «нет»)
        if (!_inLauncher) return;

        await Bar.PollAsync(Page == LauncherPage.Bar, ct);
        if (_needApps || now - _appsAt >= AppsRefreshEvery) await LoadAppsAsync(now, ct);
        else if (now - _availabilityAt >= AvailabilityEvery) Rebuild();   // игру могли установить или удалить
    }

    // ---------- внутреннее ----------
    private async Task LoadAppsAsync(DateTimeOffset now, CancellationToken ct)
    {
        _needApps = false;
        var r = await _client.SendAsync(new IpcRequest(Interlocked.Increment(ref _nextId), Ops.Apps), ct);
        if (r.Ok && r.Apps is not null)
        {
            _apps = r.Apps;
            _appsLoaded = true;
            _appsAt = _apps.Any(a => a.CoverVersion is not null && ImageFiles.Safe(a.CoverPath, _coversDir) is null)
                ? now - AppsRefreshEvery + CoversRetryAfter    // у части игр обложка ещё не скачана: следующий запрос через несколько секунд
                : now;
            Rebuild();
        }
        else
        {
            _appsAt = now - AppsRefreshEvery + AppsRetryAfter;   // старый список остаётся; повторим скоро
            Rebuild();
        }
    }

    private void OnSnapshot(AgentSnapshot _)
    {
        var inLauncher = Flow.Screen == Screen.Cabinet || Flow.HasSession;
        var hasSession = Flow.HasSession;

        if (inLauncher && !_inLauncher)
        {
            _needApps = true;
            SelectPage(hasSession ? LauncherPage.Library : LauncherPage.Tariffs);
        }
        else if (!inLauncher && _inLauncher)
        {
            _search = "";
            _category = "";
            OnChanged(nameof(Search));
            LaunchError = null;
            Bar.Reset();   // корзина, заказы и вызов не должны достаться следующему гостю
            Notice = null;
        }
        else if (inLauncher && hasSession != _hadSession)
        {
            // сессия началась: к играм; закончилась: снова к выбору тарифа
            if (hasSession) { if (Page == LauncherPage.Tariffs) SelectPage(LauncherPage.Library); }
            else SelectPage(LauncherPage.Tariffs);
        }
        _inLauncher = inLauncher;
        _hadSession = hasSession;
        OnChanged(nameof(CanLaunch));
        OnChanged(nameof(CanLogout));
        OnChanged(nameof(CanExtend));
        OnChanged(nameof(ExtendIsPrimary));
        OnChanged(nameof(ExtendIsOutline));
        OnChanged(nameof(ShowNoExtendHint));
        OnChanged(nameof(ShowTariffsTab));
        OnChanged(nameof(TariffsTitle));
        OnChanged(nameof(PageTitle));
        if (hasSession && Page == LauncherPage.Tariffs && !Flow.CanExtend) SelectPage(LauncherPage.Library);   // продлевать нечем: вкладка опустела
    }

    /// <summary>Показать сообщение об успехе на несколько секунд.</summary>
    public void ShowNotice(string text)
    {
        _noticeAt = _time.GetUtcNow();
        Notice = text;
    }

    private void ShowError(string text)
    {
        _errorAt = _time.GetUtcNow();
        LaunchError = text;
    }

    private void Rebuild()
    {
        _availabilityAt = _time.GetUtcNow();
        if (Page == LauncherPage.Bar)
        {
            ClearLibrary();
            var (productCards, barChips) = Bar.Build(_search, _category);
            if (!barChips.Any(c => c.Selected && c.Key.Length > 0)) _category = "";
            if (!barChips.SequenceEqual(Chips)) Chips = barChips;
            if (!productCards.SequenceEqual(ProductCards)) ProductCards = productCards; else OnChanged(nameof(BarEmptyText));
            return;
        }
        if (ProductCards.Count > 0) ProductCards = [];
        if (Page == LauncherPage.Tariffs)
        {
            ClearLibrary();
            if (Chips.Count > 0) Chips = [];
            return;
        }

        // библиотека: сверху игры, ниже программы; категорий нет, поиск работает по обоим разделам
        if (Chips.Count > 0) Chips = [];
        _category = "";
        var needle = _search.Trim();
        bool Matches(AppInfo a) => needle.Length == 0 || Ru.CompareInfo.IndexOf(a.Title, needle, CompareOptions.IgnoreCase) >= 0;
        var gameCards = _apps.Where(a => a.Tab == "games").Where(Matches).Select(ToCard).ToList();
        var appCards = _apps.Where(a => a.Tab == "apps").Where(Matches).Select(ToCard).ToList();
        // списки заменяем только при реальных изменениях: иначе окно пересоздавало бы карточки при каждом опросе
        if (!gameCards.SequenceEqual(GameCards)) GameCards = gameCards;
        if (!appCards.SequenceEqual(AppCards)) AppCards = appCards;
        OnChanged(nameof(EmptyText));
    }

    private void ClearLibrary()
    {
        if (GameCards.Count > 0) GameCards = [];
        if (AppCards.Count > 0) AppCards = [];
    }

    private AppCard ToCard(AppInfo a)
    {
        var available = _launcher.IsAvailable(a);
        var initial = a.Title.FirstOrDefault(char.IsLetterOrDigit);
        return new(a, a.Title, initial == default ? "?" : char.ToUpper(initial, Ru).ToString(), a.Category,
            a.AgeRating is int age ? $"{age}+" : "", available, available ? null : "Не установлена", ImageFiles.Safe(a.CoverPath, _coversDir));
    }

    private void RaisePageProperties()
    {
        foreach (var name in new[] { nameof(IsLibraryPage), nameof(IsBarPage), nameof(IsTariffsPage), nameof(ShowCatalog), nameof(ShowGames), nameof(ShowApps), nameof(ExtendIsPrimary), nameof(ExtendIsOutline),
                                     nameof(ShowBar), nameof(ShowSearch), nameof(PageTitle), nameof(EmptyText), nameof(BarEmptyText) })
            OnChanged(name);
    }

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
