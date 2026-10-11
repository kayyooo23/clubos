using ClubOS.Shared;
using ClubOS.Shared.Ui;
using Microsoft.Extensions.Time.Testing;

namespace ClubOS.Tests;

/// <summary>Продление сессии на стороне клиента: какие карточки предлагать, как это уходит на сервер, как ведёт себя меню; пополнение баланса через администратора.</summary>
public class ExtendClientTests
{
    private static AgentSnapshot Playing(int balance = 250, string? kind = "package", int left = 3600) => new(true, PcMode.GuestSession,
        new AccountInfo(2, "guest", "+7 *** ***-00-01", balance),
        new SessionInfo(1, 1, 2, "3 часа", DateTime.UtcNow, null, null, 250, left, Kind: kind), null, left);

    private static readonly AgentSnapshot Cabinet = new(true, PcMode.Locked, new AccountInfo(2, "guest", "+7 *** ***-00-01", 500), null, null, null);

    private static readonly IReadOnlyList<TariffInfo> Tariffs = new List<TariffInfo>
    {
        new(1, "Час", "hourly", 120, null, null, null),
        new(2, "3 часа", "package", 250, 180, null, null),
        new(3, "Час пакетом", "package", 100, 60, null, null),
        new(4, "Ночь", "night", 400, null, 23, 8),
    };

    private sealed class NoGames : IAppLauncher
    {
        public bool IsAvailable(AppInfo app) => true;
        public string? Launch(AppInfo app) => null;
    }

    private sealed record Rig(FlowController Flow, LauncherModel Model, FakeAgent Agent, FakeTimeProvider Time);

    private static Rig Create(IReadOnlyList<TariffInfo>? tariffs = null)
    {
        var agent = new FakeAgent();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        var list = tariffs ?? Tariffs;
        agent.Handler = r => r.Op switch
        {
            Ops.Tariffs => agent.Ok(r, tariffs: list),
            Ops.Apps => agent.Ok(r, apps: []),
            _ => agent.Ok(r),
        };
        agent.Current = Cabinet;
        var flow = new FlowController(agent, time);
        return new Rig(flow, new LauncherModel(flow, agent, new NoGames(), time, Path.GetTempPath()), agent, time);
    }

    private static async Task Poll(Rig r)
    {
        await r.Flow.PollAsync();
        await r.Model.PollAsync();
    }

    // ---------- какие карточки продления и когда ----------
    [Fact]
    public async Task During_a_package_session_only_packages_are_offered_for_extension()
    {
        var r = Create();
        await Poll(r);                                      // тарифы подгрузились ещё в кабинете
        r.Agent.Current = Playing();
        await Poll(r);
        Assert.True(r.Flow.CanExtend);
        Assert.Equal(["3 часа", "Час пакетом"], r.Flow.ExtendTariffs.Select(t => t.Name));   // ни почасового, ни ночи
        var three = r.Flow.ExtendTariffs[0];
        Assert.Equal("250 ₽", three.PriceText);
        Assert.Equal("Добавить 3 ч к вашему времени", three.Description);
        Assert.True(three.CanStart);
    }

    [Fact]
    public async Task Tariffs_are_loaded_during_a_session_too_even_if_the_guest_started_it_straight_from_a_cold_menu()
    {
        var r = Create();
        r.Agent.Current = Playing();                         // меню открылось уже во время игры (оверлей перезапустили)
        await Poll(r);
        Assert.Equal(Screen.Login, r.Flow.Screen);
        Assert.Single(r.Agent.Ops(Ops.Tariffs));
        Assert.True(r.Flow.CanExtend);
    }

    [Fact]
    public async Task An_hourly_session_has_nothing_to_extend()
    {
        var r = Create();
        r.Agent.Current = Playing(kind: "hourly");
        await Poll(r);
        Assert.False(r.Flow.CanExtend);
        Assert.False(r.Model.CanExtend);
        Assert.False(r.Model.ShowTariffsTab);               // вкладки продления у почасовой сессии нет
        Assert.True(r.Model.ShowNoExtendHint);
    }

    [Fact]
    public async Task No_packages_in_the_club_means_no_extension()
    {
        var r = Create([new TariffInfo(1, "Час", "hourly", 120, null, null, null)]);
        r.Agent.Current = Playing();
        await Poll(r);
        Assert.False(r.Flow.CanExtend);
    }

    [Fact]
    public async Task A_package_that_the_balance_does_not_cover_is_marked_with_what_is_missing()
    {
        var r = Create();
        r.Agent.Current = Playing(balance: 150);
        await Poll(r);
        var three = r.Flow.ExtendTariffs.Single(t => t.Name == "3 часа");
        Assert.False(three.CanStart);
        Assert.Equal("Не хватает 100 ₽", three.Hint);
        Assert.True(r.Flow.ExtendTariffs.Single(t => t.Name == "Час пакетом").CanStart);
    }

    // ---------- само продление ----------
    [Fact]
    public async Task Extending_sends_the_chosen_package_and_reports_success()
    {
        var r = Create();
        r.Agent.Current = Playing();
        await Poll(r);
        Assert.True(await r.Flow.ExtendAsync(2));
        var sent = r.Agent.Ops(Ops.SessionExtend).Single();
        Assert.Equal(2, sent.TariffId);
        Assert.Null(r.Flow.Error);
    }

    [Fact]
    public async Task A_refusal_from_the_server_is_shown_and_reported_as_a_failure()
    {
        var r = Create();
        r.Agent.Current = Playing();
        await Poll(r);
        var inner = r.Agent.Handler;
        r.Agent.Handler = req => req.Op == Ops.SessionExtend ? IpcResponse.Fail(req.Id, 402, "Не хватает 61 ₽ на балансе", r.Agent.Current) : inner(req);
        Assert.False(await r.Flow.ExtendAsync(2));
        Assert.Equal("Не хватает 61 ₽ на балансе", r.Flow.Error);
    }

    // ---------- меню ----------
    [Fact]
    public async Task During_a_session_the_tariffs_tab_becomes_extension_and_returns_to_tariffs_afterwards()
    {
        var r = Create();
        await Poll(r);
        Assert.Equal("Тарифы", r.Model.TariffsTitle);
        r.Agent.Current = Playing();
        await Poll(r);
        Assert.Equal("Продление", r.Model.TariffsTitle);
        Assert.True(r.Model.ShowTariffsTab);
        r.Model.SelectPage(LauncherPage.Tariffs);
        Assert.Equal("Продление", r.Model.PageTitle);
        await Poll(r);
        Assert.Equal(LauncherPage.Tariffs, r.Model.Page);   // опрос гостя со страницы продления не уводит

        r.Agent.Current = Cabinet;                          // сессия кончилась
        await Poll(r);
        Assert.Equal(LauncherPage.Tariffs, r.Model.Page);
        Assert.Equal("Тарифы", r.Model.PageTitle);
    }

    [Fact]
    public async Task The_extension_page_closes_by_itself_when_there_is_nothing_left_to_extend()
    {
        var r = Create();
        r.Agent.Current = Playing();
        await Poll(r);
        r.Model.SelectPage(LauncherPage.Tariffs);
        r.Agent.Current = Playing(kind: "hourly");          // сессия поменялась на такую, которую продлить нельзя
        await Poll(r);
        Assert.Equal(LauncherPage.Library, r.Model.Page);
    }

    [Fact]
    public async Task A_success_notice_shows_for_a_few_seconds()
    {
        var r = Create();
        r.Agent.Current = Playing();
        await Poll(r);
        r.Model.ShowNotice("Время добавлено: 3 часа");
        Assert.Equal("Время добавлено: 3 часа", r.Model.Notice);
        r.Time.Advance(TimeSpan.FromSeconds(11));
        await Poll(r);
        Assert.Null(r.Model.Notice);
    }

    // ---------- служба ----------
    [Fact]
    public async Task The_service_posts_the_extension_with_the_guest_token_and_does_not_log_the_guest_out_on_a_refusal()
    {
        var (core, server, _) = Make.Core();
        server.On("GET", "/api/agent/state", 200, Samples.Cabinet).On("POST", "/api/auth/login", 200, Samples.LoginOk)
              .On("POST", "/api/me/sessions/extend", 200, """{"id":1,"balance":250,"seconds_left":21600}""");
        await core.HandleAsync(new IpcRequest(1, Ops.Login, Phone: "+79000000001", Password: "p"));
        var ok = await core.HandleAsync(new IpcRequest(2, Ops.SessionExtend, TariffId: 2));
        Assert.True(ok.Ok);
        var seen = server.To("/api/me/sessions/extend").Single();
        Assert.Equal("""{"tariff_id":2}""", seen.Body);
        Assert.Equal($"Bearer {FakeServer.GuestToken}", seen.Auth);

        server.On("POST", "/api/me/sessions/extend", 402, Samples.Error("Не хватает 61 ₽ на балансе"));
        var refused = await core.HandleAsync(new IpcRequest(3, Ops.SessionExtend, TariffId: 2));
        Assert.False(refused.Ok);
        Assert.Equal("Не хватает 61 ₽ на балансе", refused.Error);
        Assert.Equal("guest", refused.Snapshot!.Account?.Role);   // отказ по деньгам гостя не разлогинивает
    }

    // ---------- пополнение баланса ----------
    [Fact]
    public async Task Topping_up_calls_the_administrator_with_a_clear_reason_and_blocks_a_second_call()
    {
        var r = Create();
        r.Agent.Current = Playing();
        var inner = r.Agent.Handler;
        r.Agent.Handler = req => req.Op == Ops.CallAdmin ? new IpcResponse(req.Id, true, 200, null, r.Agent.Current, OpenCall: new CallInfo(5, "open", req.Text)) : inner(req);
        await Poll(r);
        await r.Model.Bar.TopUpAsync();
        Assert.Equal("Пополнение баланса", r.Agent.Ops(Ops.CallAdmin).Single().Text);
        Assert.Equal("Администратор скоро подойдёт, чтобы пополнить баланс", r.Model.Bar.Note);
        Assert.True(r.Model.Bar.CallOpen);
        Assert.False(r.Model.Bar.CanCall);                  // обе кнопки («Пополнить» и «Вызвать») недоступны, пока вызов открыт
        await r.Model.Bar.TopUpAsync();
        await r.Model.Bar.CallAdminAsync();
        Assert.Single(r.Agent.Ops(Ops.CallAdmin));
    }

    [Fact]
    public async Task The_plain_call_still_has_no_text_and_its_own_message()
    {
        var r = Create();
        r.Agent.Current = Playing();
        await Poll(r);
        await r.Model.Bar.CallAdminAsync();
        Assert.Null(r.Agent.Ops(Ops.CallAdmin).Single().Text);
        Assert.Equal("Администратор вызван, скоро подойдёт", r.Model.Bar.Note);
    }
}
