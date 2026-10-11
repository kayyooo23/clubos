using ClubOS.Shared;
using ClubOS.Shared.Ui;
using Microsoft.Extensions.Time.Testing;

namespace ClubOS.Tests;

public class LauncherModelTests
{
    private static readonly AgentSnapshot LockedNoAccount = new(true, PcMode.Locked, null, null, null, null);
    private static AgentSnapshot Cabinet(int balance = 500) =>
        new(true, PcMode.Locked, new AccountInfo(2, "guest", "+7 *** ***-00-01", balance), null, null, null);
    private static AgentSnapshot Playing(int left = 3600) => new(true, PcMode.GuestSession,
        new AccountInfo(2, "guest", "+7 *** ***-00-01", 250),
        new SessionInfo(1, 1, 2, "3 часа", DateTime.UtcNow, null, null, 250, left), null, left);

    private static readonly IReadOnlyList<AppInfo> Catalog = new List<AppInfo>
    {
        new(1, "Dota 2", "games", "MOBA", 12, "steam", "570"),
        new(2, "Counter-Strike 2", "games", "Шутеры", 16, "steam", "730"),
        new(3, "Apex Legends", "games", "Шутеры", 16, "steam", "1172470"),
        new(4, "Chrome", "apps", "Браузеры", null, "exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe"),
    };

    private sealed class FakeLauncher : IAppLauncher
    {
        public HashSet<int> Missing { get; } = [];
        public List<AppInfo> Started { get; } = [];
        public string? Error { get; set; }
        public bool IsAvailable(AppInfo app) => !Missing.Contains(app.Id);
        public string? Launch(AppInfo app) { if (Error is null) Started.Add(app); return Error; }
    }

    private static (LauncherModel Model, FlowController Flow, FakeAgent Agent, FakeLauncher Launcher, FakeTimeProvider Time) Create()
    {
        var agent = new FakeAgent();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
        agent.Handler = r => r.Op switch
        {
            Ops.Tariffs => agent.Ok(r, tariffs: []),
            Ops.Apps => agent.Ok(r, apps: Catalog),
            _ => agent.Ok(r),
        };
        agent.Current = LockedNoAccount;
        var flow = new FlowController(agent, time);
        var launcher = new FakeLauncher();
        return (new LauncherModel(flow, agent, launcher, time), flow, agent, launcher, time);
    }

    private static async Task Poll(FlowController flow, LauncherModel model)
    {
        await flow.PollAsync();
        await model.PollAsync();
    }

    private static async Task<(LauncherModel Model, FlowController Flow, FakeAgent Agent, FakeLauncher Launcher, FakeTimeProvider Time)> InSession()
    {
        var c = Create();
        c.Agent.Current = Cabinet();
        await Poll(c.Flow, c.Model);
        c.Agent.Current = Playing();
        await Poll(c.Flow, c.Model);
        return c;
    }

    // ---------- когда видно меню и что на нём открыто ----------
    [Fact]
    public async Task Catalog_is_not_requested_until_the_guest_is_in()
    {
        var (model, flow, agent, _, _) = Create();
        await Poll(flow, model);
        Assert.Empty(agent.Ops(Ops.Apps));
        Assert.Empty(model.GameCards);
        Assert.Empty(model.AppCards);
    }

    [Fact]
    public async Task Cabinet_opens_on_tariffs_and_loads_the_catalog_in_the_background()
    {
        var (model, flow, agent, _, _) = Create();
        agent.Current = Cabinet();
        await Poll(flow, model);
        Assert.Equal(LauncherPage.Tariffs, model.Page);
        Assert.True(model.IsTariffsPage);
        Assert.Equal("Тарифы", model.PageTitle);
        Assert.False(model.ShowCatalog);
        Assert.Single(agent.Ops(Ops.Apps));
        Assert.True(model.CanLogout);
        Assert.False(model.CanLaunch);
    }

    [Fact]
    public async Task Session_start_switches_to_the_library_and_end_switches_back_to_tariffs()
    {
        var (model, flow, agent, _, _) = Create();
        agent.Current = Cabinet();
        await Poll(flow, model);

        agent.Current = Playing();
        await Poll(flow, model);
        Assert.Equal(LauncherPage.Library, model.Page);
        Assert.Equal("Библиотека", model.PageTitle);
        Assert.True(model.CanLaunch);
        Assert.False(model.CanLogout);       // во время игры из кабинета не выйти
        Assert.Equal(3, model.GameCards.Count);
        Assert.Single(model.AppCards);

        agent.Current = Cabinet();           // сессия закончилась, гость ещё в кабинете
        await Poll(flow, model);
        Assert.Equal(LauncherPage.Tariffs, model.Page);
        Assert.False(model.CanLaunch);
    }

    [Fact]
    public async Task One_library_tab_holds_games_on_top_and_programs_below()
    {
        var (model, flow, _, _, _) = await InSession();
        Assert.Equal(LauncherPage.Library, model.Page);
        Assert.Equal(["Dota 2", "Counter-Strike 2", "Apex Legends"], model.GameCards.Select(c => c.Title));
        Assert.Equal(["Chrome"], model.AppCards.Select(c => c.Title));
        Assert.True(model.ShowGames);
        Assert.True(model.ShowApps);
        await Poll(flow, model);
        Assert.Equal(LauncherPage.Library, model.Page);   // опрос вкладку не меняет
    }

    [Fact]
    public async Task A_section_without_items_is_hidden_and_the_other_one_stays()
    {
        var c = Create();
        c.Agent.Handler = r => r.Op == Ops.Apps ? c.Agent.Ok(r, apps: Catalog.Where(a => a.Tab == "apps").ToList()) : c.Agent.Ok(r);   // в клубе только программы
        c.Agent.Current = Cabinet();
        await Poll(c.Flow, c.Model);
        c.Agent.Current = Playing();
        await Poll(c.Flow, c.Model);
        Assert.False(c.Model.ShowGames);
        Assert.True(c.Model.ShowApps);
        Assert.Empty(c.Model.Chips);
        Assert.Null(c.Model.EmptyText);
    }

    // ---------- поиск и категории ----------
    [Fact]
    public async Task The_library_has_no_category_chips()
    {
        var (model, _, _, _, _) = await InSession();
        Assert.Empty(model.Chips);
        model.SelectCategory("Шутеры");                     // старый вызов ничего не фильтрует в библиотеке
        Assert.Equal(3, model.GameCards.Count);
    }

    [Fact]
    public async Task Search_ignores_case_and_works_with_russian_and_latin_text()
    {
        var (model, _, _, _, _) = await InSession();
        model.Search = "  counter ";
        Assert.Equal(["Counter-Strike 2"], model.GameCards.Select(c => c.Title));
        Assert.Empty(model.AppCards);
        model.Search = "APEX";
        Assert.Equal(["Apex Legends"], model.GameCards.Select(c => c.Title));
        model.Search = "нет такой";
        Assert.Empty(model.GameCards);
        Assert.Empty(model.AppCards);
        Assert.Equal("Ничего не найдено", model.EmptyText);
        model.Search = "";
        Assert.Equal(3, model.GameCards.Count);
        Assert.Single(model.AppCards);
        Assert.Null(model.EmptyText);
    }

    [Fact]
    public async Task Search_covers_both_sections_and_hides_the_one_without_matches()
    {
        var (model, _, _, _, _) = await InSession();
        model.Search = "chrome";
        Assert.Empty(model.GameCards);
        Assert.Equal(["Chrome"], model.AppCards.Select(c => c.Title));
        Assert.False(model.ShowGames);                      // раздел игр без совпадений скрыт целиком
        Assert.True(model.ShowApps);
        Assert.Null(model.EmptyText);
        model.Search = "dota";
        Assert.Equal(["Dota 2"], model.GameCards.Select(c => c.Title));
        Assert.False(model.ShowApps);
    }

    [Fact]
    public async Task Switching_page_clears_the_search()
    {
        var (model, _, _, _, _) = await InSession();
        model.Search = "dota";
        model.SelectPage(LauncherPage.Bar);
        Assert.Equal("", model.Search);
        Assert.Empty(model.GameCards);                      // на других вкладках библиотека не держит списки
        model.SelectPage(LauncherPage.Library);
        Assert.Equal(3, model.GameCards.Count);
        Assert.Single(model.AppCards);
    }

    [Fact]
    public async Task Card_shows_initial_and_age_and_does_not_replace_the_list_on_every_poll()
    {
        var (model, flow, _, _, _) = await InSession();
        var dota = model.GameCards[0];
        Assert.Equal("D", dota.Initial);
        Assert.Equal("12+", dota.AgeText);
        var before = model.GameCards;
        await Poll(flow, model);
        await Poll(flow, model);
        Assert.Same(before, model.GameCards);   // иначе окно пересоздавало бы карточки и клик мог потеряться
    }

    // ---------- запуск ----------
    [Fact]
    public async Task Launch_starts_the_game_and_tells_the_window_to_step_aside()
    {
        var (model, _, _, launcher, _) = await InSession();
        AppInfo? started = null;
        model.AppLaunched += a => started = a;
        model.Launch(model.GameCards[0]);
        Assert.Equal("Dota 2", launcher.Started.Single().Title);
        Assert.Equal("Dota 2", started?.Title);
        Assert.Null(model.LaunchError);
    }

    [Fact]
    public async Task A_program_starts_the_same_way_as_a_game()
    {
        var (model, _, _, launcher, _) = await InSession();
        AppInfo? started = null;
        model.AppLaunched += a => started = a;
        model.Launch(model.AppCards[0]);
        Assert.Equal("Chrome", launcher.Started.Single().Title);
        Assert.Equal("Chrome", started?.Title);
    }

    [Fact]
    public async Task Launch_without_a_session_sends_the_guest_to_tariffs_and_starts_nothing()
    {
        var (model, flow, agent, launcher, _) = Create();
        agent.Current = Cabinet();
        await Poll(flow, model);
        model.SelectPage(LauncherPage.Library);
        model.Launch(model.GameCards[0]);
        Assert.Empty(launcher.Started);
        Assert.Equal(LauncherPage.Tariffs, model.Page);
        Assert.Contains("выберите тариф", model.LaunchError);
    }

    [Fact]
    public async Task Missing_game_is_marked_and_cannot_be_started()
    {
        var c = Create();
        c.Launcher.Missing.Add(2);
        c.Agent.Current = Cabinet();
        await Poll(c.Flow, c.Model);
        c.Agent.Current = Playing();
        await Poll(c.Flow, c.Model);
        var cs = c.Model.GameCards.Single(x => x.Id == 2);
        Assert.False(cs.Available);
        Assert.Equal("Не установлена", cs.Hint);

        c.Model.Launch(cs);
        Assert.Empty(c.Launcher.Started);
        Assert.Contains("не установлена", c.Model.LaunchError);
    }

    [Fact]
    public async Task Launch_failure_is_shown_and_does_not_minimize_the_menu()
    {
        var (model, _, _, launcher, _) = await InSession();
        launcher.Error = "Не удалось запустить";
        var fired = false;
        model.AppLaunched += _ => fired = true;
        model.Launch(model.GameCards[0]);
        Assert.Equal("Не удалось запустить", model.LaunchError);
        Assert.False(fired);
    }

    [Fact]
    public async Task Launch_error_disappears_by_itself()
    {
        var (model, flow, _, launcher, time) = await InSession();
        launcher.Error = "Ошибка";
        model.Launch(model.GameCards[0]);
        Assert.NotNull(model.LaunchError);
        time.Advance(TimeSpan.FromSeconds(11));
        await Poll(flow, model);
        Assert.Null(model.LaunchError);
    }

    // ---------- загрузка каталога ----------
    [Fact]
    public async Task Failed_catalog_load_keeps_the_old_list_and_retries_soon()
    {
        var (model, flow, agent, _, time) = await InSession();
        Assert.Equal(3, model.GameCards.Count);

        agent.Handler = r => r.Op == Ops.Apps ? agent.Fail(r, 0, "нет связи") : agent.Ok(r);
        time.Advance(TimeSpan.FromMinutes(6));
        await Poll(flow, model);
        Assert.Equal(3, model.GameCards.Count);                // список не пропал
        var sent = agent.Ops(Ops.Apps).Count();

        await Poll(flow, model);                           // сразу же заново не долбим
        Assert.Equal(sent, agent.Ops(Ops.Apps).Count());
        time.Advance(TimeSpan.FromSeconds(16));
        await Poll(flow, model);
        Assert.Equal(sent + 1, agent.Ops(Ops.Apps).Count());
    }

    [Fact]
    public async Task Empty_catalog_explains_what_to_do()
    {
        var (model, flow, agent, _, _) = Create();
        agent.Handler = r => r.Op == Ops.Apps ? agent.Ok(r, apps: []) : agent.Ok(r);
        agent.Current = Cabinet();
        await Poll(flow, model);
        agent.Current = Playing();
        await Poll(flow, model);
        Assert.Empty(model.GameCards);
        Assert.Empty(model.AppCards);
        Assert.False(model.ShowGames);
        Assert.False(model.ShowApps);
        Assert.StartsWith("Список пока пуст", model.EmptyText);
    }

    [Fact]
    public async Task Catalog_is_refreshed_every_few_minutes_while_the_guest_is_in()
    {
        var (model, flow, agent, _, time) = await InSession();
        var before = agent.Ops(Ops.Apps).Count();
        time.Advance(TimeSpan.FromMinutes(4));
        await Poll(flow, model);
        Assert.Equal(before, agent.Ops(Ops.Apps).Count());
        time.Advance(TimeSpan.FromMinutes(2));
        await Poll(flow, model);
        Assert.Equal(before + 1, agent.Ops(Ops.Apps).Count());
    }

    [Fact]
    public async Task Leaving_the_cabinet_clears_the_menu_state()
    {
        var (model, flow, agent, _, _) = Create();
        agent.Current = Cabinet();
        await Poll(flow, model);
        model.SelectPage(LauncherPage.Library);
        model.Search = "dota";
        agent.Current = LockedNoAccount;
        await Poll(flow, model);
        Assert.Equal("", model.Search);
    }

    [Fact]
    public async Task Radio_style_properties_select_the_page()
    {
        var (model, _, _, _, _) = await InSession();
        model.IsBarPage = true;
        Assert.Equal(LauncherPage.Bar, model.Page);
        Assert.False(model.IsLibraryPage);
        model.IsLibraryPage = false;                // снятие галочки ничего не меняет
        Assert.Equal(LauncherPage.Bar, model.Page);
        model.IsLibraryPage = true;
        Assert.Equal(LauncherPage.Library, model.Page);
    }
}
