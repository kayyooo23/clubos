using ClubOS.Shared;
using ClubOS.Shared.Ui;
using Microsoft.Extensions.Time.Testing;

namespace ClubOS.Tests;

public class FlowControllerTests
{
    private static readonly AgentSnapshot LockedNoAccount = new(true, PcMode.Locked, null, null, null, null);
    private static AgentSnapshot Cabinet(int balance = 500) =>
        new(true, PcMode.Locked, new AccountInfo(2, "guest", "+7 *** ***-00-01", balance), null, null, null);
    private static AgentSnapshot Playing(int left = 3600) => new(true, PcMode.GuestSession,
        new AccountInfo(2, "guest", "+7 *** ***-00-01", 250),
        new SessionInfo(1, 1, 2, "3 часа", DateTime.UtcNow, null, null, 250, left), null, left);
    private static readonly AgentSnapshot StaffDesk = new(true, PcMode.StaffDesktop, new AccountInfo(null, "staff", null, null), null, null, null);

    private static readonly IReadOnlyList<TariffInfo> Tariffs = new List<TariffInfo>
    {
        new(1, "Час", "hourly", 120, null, null, null),
        new(2, "3 часа", "package", 250, 180, null, null),
        new(3, "Ночь", "night", 400, null, 23, 8),
    };

    private static (FlowController Flow, FakeAgent Agent, FakeTimeProvider Time) Create()
    {
        var agent = new FakeAgent();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
        agent.Handler = r => r.Op switch
        {
            Ops.Tariffs => agent.Ok(r, tariffs: Tariffs),
            _ => agent.Ok(r),
        };
        agent.Current = LockedNoAccount;
        return (new FlowController(agent, time), agent, time);
    }

    // ---------- маршрутизация экранов ----------
    [Fact]
    public async Task Starts_on_login_and_shows_a_banner_while_offline()
    {
        var (flow, agent, _) = Create();
        agent.Current = AgentSnapshot.Booting with { Message = "Нет связи с сервером" };
        await flow.PollAsync();
        Assert.Equal(Screen.Login, flow.Screen);
        Assert.True(flow.IsOffline);
        Assert.Equal("Нет связи с сервером", flow.Banner);
    }

    [Fact]
    public async Task Service_down_shows_a_banner_instead_of_crashing()
    {
        var (flow, agent, _) = Create();
        agent.Handler = r => IpcResponse.Fail(r.Id, 0, "Служба ClubOS недоступна");
        await flow.PollAsync();
        Assert.Equal("Служба ClubOS недоступна", flow.Banner);
        Assert.Equal(PcMode.Locked, flow.Snapshot.Mode);
    }

    [Fact]
    public async Task Guest_account_on_a_locked_pc_opens_the_cabinet_with_tariffs()
    {
        var (flow, agent, _) = Create();
        agent.Current = Cabinet(300);
        await flow.PollAsync();
        Assert.Equal(Screen.Cabinet, flow.Screen);
        Assert.Equal(300, flow.Balance);
        Assert.Equal("+7 *** ***-00-01", flow.CabinetPhone);
        Assert.Equal(3, flow.Tariffs.Count);
        var byName = flow.Tariffs.ToDictionary(t => t.Name);
        Assert.True(byName["Час"].CanStart);            // 20 ₽ достаточно
        Assert.True(byName["3 часа"].CanStart);         // 250 <= 300
        Assert.False(byName["Ночь"].CanStart);
        Assert.Equal("Не хватает 100 ₽", byName["Ночь"].Hint);
    }

    [Fact]
    public async Task Tariff_list_is_not_replaced_on_every_poll()
    {
        // иначе окно пересоздаёт кнопки тарифов каждую секунду и клик мышью может не сработать
        var (flow, agent, _) = Create();
        agent.Current = Cabinet(300);
        await flow.PollAsync();
        var first = flow.Tariffs;
        var changes = 0;
        flow.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(FlowController.Tariffs)) changes++; };
        for (var i = 0; i < 5; i++) await flow.PollAsync();
        Assert.Same(first, flow.Tariffs);
        Assert.Equal(0, changes);
        agent.Current = Cabinet(600);                 // а при реальном изменении баланса — заменяется
        await flow.PollAsync();
        Assert.NotSame(first, flow.Tariffs);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task Tariff_availability_follows_the_balance_when_it_changes()
    {
        var (flow, agent, _) = Create();
        agent.Current = Cabinet(100);
        await flow.PollAsync();
        Assert.False(flow.Tariffs.Single(t => t.Name == "3 часа").CanStart);
        agent.Current = Cabinet(600);       // сотрудник пополнил баланс
        await flow.PollAsync();
        Assert.True(flow.Tariffs.Single(t => t.Name == "3 часа").CanStart);
        Assert.Single(agent.Ops(Ops.Tariffs));            // тарифы не перезапрашиваются из-за баланса
    }

    [Fact]
    public async Task Tariffs_are_refreshed_once_a_minute_while_in_the_cabinet()
    {
        var (flow, agent, time) = Create();
        agent.Current = Cabinet();
        await flow.PollAsync();
        time.Advance(TimeSpan.FromSeconds(30)); await flow.PollAsync();
        Assert.Single(agent.Ops(Ops.Tariffs));
        time.Advance(TimeSpan.FromSeconds(31)); await flow.PollAsync();
        Assert.Equal(2, agent.Ops(Ops.Tariffs).Count());
    }

    [Fact]
    public async Task Session_and_staff_modes_reset_the_screen_to_login()
    {
        var (flow, agent, _) = Create();
        agent.Current = Cabinet();
        await flow.PollAsync();
        flow.Password = "typed";
        agent.Current = Playing();
        await flow.PollAsync();
        Assert.True(flow.HasSession);
        Assert.Equal("3 часа", flow.SessionTitle);
        Assert.Equal("1:00:00", flow.SessionClock);
        Assert.Equal("", flow.Password);               // пароли не висят в памяти после входа
        agent.Current = StaffDesk;
        await flow.PollAsync();
        Assert.Equal(Screen.Login, flow.Screen);
    }

    [Fact]
    public async Task Hourly_session_shows_no_countdown()
    {
        var (flow, agent, _) = Create();
        agent.Current = Playing() with { SecondsLeft = null };
        await flow.PollAsync();
        Assert.Equal("по факту", flow.SessionClock);
    }

    [Fact]
    public async Task Server_side_logout_returns_to_login_and_clears_the_form()
    {
        var (flow, agent, _) = Create();
        agent.Current = Cabinet();
        await flow.PollAsync();
        agent.Current = LockedNoAccount;                 // сервер вышел по бездействию
        await flow.PollAsync();
        Assert.Equal(Screen.Login, flow.Screen);
        Assert.Equal("", flow.Password);
    }

    [Fact]
    public async Task Snapshot_changes_are_announced_for_the_windows()
    {
        var (flow, agent, _) = Create();
        var seen = new List<PcMode>();
        flow.SnapshotChanged += s => seen.Add(s.Mode);
        agent.Current = Playing(); await flow.PollAsync();
        agent.Current = StaffDesk; await flow.PollAsync();
        agent.Current = LockedNoAccount; await flow.PollAsync();
        Assert.Equal([PcMode.GuestSession, PcMode.StaffDesktop, PcMode.Locked], seen);
    }

    [Fact]
    public async Task Lock_screen_shows_the_club_and_pc_names_as_typed_without_the_edge_spaces()
    {
        var (flow, agent, _) = Create();
        Assert.Equal("Club OS", flow.ClubTitle);                       // пока сервер не ответил
        Assert.Equal("", flow.PcLabel);
        agent.Current = LockedNoAccount with { ClubName = "  Кибер-зона на Лиговском ", PcName = "pc-7" };
        await flow.PollAsync();
        Assert.Equal("Кибер-зона на Лиговском", flow.ClubTitle);
        Assert.Equal("pc-7", flow.PcLabel);
        agent.Current = LockedNoAccount with { ClubName = "", PcName = " " };
        await flow.PollAsync();
        Assert.Equal("Club OS", flow.ClubTitle);                       // пустое название не оставляет дыру на экране
        Assert.Equal("", flow.PcLabel);
    }

    [Fact]
    public async Task Tariff_cards_carry_a_big_price_separate_from_the_conditions()
    {
        var (flow, agent, _) = Create();
        agent.Current = Cabinet(300);
        await flow.PollAsync();
        var byName = flow.Tariffs.ToDictionary(t => t.Name);
        Assert.Equal("120 ₽/ч", byName["Час"].PriceText);
        Assert.Equal("250 ₽", byName["3 часа"].PriceText);
        Assert.Equal("С 23:00 до 08:00", byName["Ночь"].Description);
        Assert.DoesNotContain("₽", byName["3 часа"].Description);     // цена не дублируется в условиях
    }

    // ---------- вход ----------
    [Fact]
    public async Task Login_validates_before_calling_the_service()
    {
        var (flow, agent, _) = Create();
        flow.Phone = "123"; flow.Password = "x";
        await flow.LoginAsync();
        Assert.Contains("номер телефона", flow.Error);
        flow.Phone = "+7 900 123-45-67"; flow.Password = "";
        await flow.LoginAsync();
        Assert.Equal("Введите пароль", flow.Error);
        Assert.Empty(agent.Sent);
    }

    [Fact]
    public async Task Login_success_clears_the_password_and_moves_to_the_cabinet()
    {
        var (flow, agent, _) = Create();
        flow.Phone = "+7 900 000-00-01"; flow.Password = "guest-pass-123";
        agent.Handler = r => r.Op == Ops.Login ? agent.Ok(r, Cabinet(500)) : agent.Ok(r, Cabinet(500), Tariffs);
        await flow.LoginAsync();
        Assert.Null(flow.Error);
        Assert.Equal("", flow.Password);
        Assert.Equal(Screen.Cabinet, flow.Screen);
        var sent = agent.Ops(Ops.Login).Single();
        Assert.Equal("+7 900 000-00-01", sent.Phone);
        Assert.Equal("guest-pass-123", sent.Password);
    }

    [Fact]
    public async Task Login_failure_shows_the_server_message_and_stays_put()
    {
        var (flow, agent, _) = Create();
        flow.Phone = "+7 900 000-00-01"; flow.Password = "bad";
        agent.Handler = r => agent.Fail(r, 401, "Неверный номер или пароль");
        await flow.LoginAsync();
        Assert.Equal("Неверный номер или пароль", flow.Error);
        Assert.Equal(Screen.Login, flow.Screen);
        Assert.False(flow.Busy);
    }

    // ---------- регистрация ----------
    [Fact]
    public async Task Registration_flow_phone_then_code_then_cabinet()
    {
        var (flow, agent, time) = Create();
        flow.GoTo(Screen.RegisterPhone);
        flow.Phone = "8 (900) 000-00-01";
        agent.Handler = r => r.Op switch
        {
            Ops.RegisterRequestCode => agent.Ok(r, resend: 60),
            Ops.RegisterConfirm => agent.Ok(r, Cabinet(0)),
            Ops.State => agent.Ok(r, LockedNoAccount),          // пока код не подтверждён, гость не вошёл
            _ => agent.Ok(r, Cabinet(0), Tariffs),
        };
        await flow.RequestCodeAsync();
        Assert.Equal(Screen.RegisterConfirm, flow.Screen);
        Assert.Equal(60, flow.ResendSecondsLeft);
        Assert.Contains("SMS", flow.Info);

        time.Advance(TimeSpan.FromSeconds(25));
        await flow.PollAsync();
        Assert.Equal(35, flow.ResendSecondsLeft);

        flow.Code = "123456"; flow.Password = "guest-pass-123"; flow.Password2 = "guest-pass-123"; flow.AcceptTerms = true;
        await flow.ConfirmAsync();
        var confirm = agent.Ops(Ops.RegisterConfirm).Single();
        Assert.True(confirm.AcceptTerms);
        Assert.Equal("123456", confirm.Code);
        Assert.Equal(Screen.Cabinet, flow.Screen);
        Assert.Equal("", flow.Code);
    }

    [Fact]
    public async Task Registration_checks_code_passwords_and_consent_locally()
    {
        var (flow, agent, _) = Create();
        flow.GoTo(Screen.RegisterPhone);
        flow.Phone = "+7 900 000-00-01";
        agent.Handler = r => agent.Ok(r, resend: 60);
        await flow.RequestCodeAsync();
        agent.Sent.Clear();

        flow.Code = "12"; flow.Password = "guest-pass-123"; flow.Password2 = "guest-pass-123"; flow.AcceptTerms = true;
        await flow.ConfirmAsync();
        Assert.Contains("6 цифр", flow.Error);
        flow.Code = "123456"; flow.Password2 = "different-pass";
        await flow.ConfirmAsync();
        Assert.Equal("Пароли не совпадают", flow.Error);
        flow.Password = flow.Password2 = "short";
        await flow.ConfirmAsync();
        Assert.Contains("минимум 8", flow.Error);
        flow.Password = flow.Password2 = "guest-pass-123"; flow.AcceptTerms = false;
        await flow.ConfirmAsync();
        Assert.Contains("согласие", flow.Error);
        Assert.Empty(agent.Sent);
    }

    [Fact]
    public async Task Wrong_code_shows_the_server_message_and_keeps_the_entered_data()
    {
        var (flow, agent, _) = Create();
        flow.GoTo(Screen.RegisterPhone);
        flow.Phone = "+7 900 000-00-01";
        agent.Handler = r => r.Op == Ops.RegisterConfirm ? agent.Fail(r, 400, "Неверный или просроченный код") : agent.Ok(r, resend: 60);
        await flow.RequestCodeAsync();
        flow.Code = "000000"; flow.Password = flow.Password2 = "guest-pass-123"; flow.AcceptTerms = true;
        await flow.ConfirmAsync();
        Assert.Equal("Неверный или просроченный код", flow.Error);
        Assert.Equal(Screen.RegisterConfirm, flow.Screen);
        Assert.Equal("guest-pass-123", flow.Password);
    }

    [Fact]
    public async Task Resend_goes_to_the_right_operation_for_each_flow()
    {
        var (flow, agent, _) = Create();
        agent.Handler = r => agent.Ok(r, resend: 60);
        flow.GoTo(Screen.ResetPhone); flow.Phone = "+7 900 000-00-01";
        await flow.RequestCodeAsync();
        Assert.Single(agent.Ops(Ops.ResetRequestCode));
        Assert.Equal(Screen.ResetConfirm, flow.Screen);
        await flow.RequestCodeAsync();            // «отправить ещё раз» с экрана подтверждения
        Assert.Equal(2, agent.Ops(Ops.ResetRequestCode).Count());
        Assert.Empty(agent.Ops(Ops.RegisterRequestCode));
    }

    [Fact]
    public async Task Password_reset_ends_on_the_login_screen_with_a_message()
    {
        var (flow, agent, _) = Create();
        agent.Handler = r => agent.Ok(r, resend: 60);
        flow.GoTo(Screen.ResetPhone); flow.Phone = "+7 900 000-00-01";
        await flow.RequestCodeAsync();
        flow.Code = "123456"; flow.Password = flow.Password2 = "brand-new-pass";
        await flow.ConfirmAsync();
        Assert.Equal(Screen.Login, flow.Screen);
        Assert.Contains("Пароль изменён", flow.Info);
        Assert.Equal("", flow.Password);
        Assert.Equal("brand-new-pass", agent.Ops(Ops.ResetConfirm).Single().Password);
    }

    // ---------- кабинет ----------
    [Fact]
    public async Task Start_stop_and_logout_go_to_the_service()
    {
        var (flow, agent, _) = Create();
        agent.Current = Cabinet();
        agent.Handler = r => r.Op == Ops.StartSession ? agent.Ok(r, Playing()) : r.Op == Ops.StopSession ? agent.Ok(r, Cabinet(250)) : r.Op == Ops.Logout ? agent.Ok(r, LockedNoAccount) : agent.Ok(r, Cabinet(), Tariffs);
        await flow.PollAsync();
        await flow.StartAsync(2);
        Assert.Equal(2, agent.Ops(Ops.StartSession).Single().TariffId);
        Assert.True(flow.HasSession);
        await flow.StopAsync();
        Assert.False(flow.HasSession);
        Assert.Equal(Screen.Cabinet, flow.Screen);
        await flow.LogoutAsync();
        Assert.Equal(Screen.Login, flow.Screen);
    }

    [Fact]
    public async Task Server_refusal_to_start_is_shown()
    {
        var (flow, agent, _) = Create();
        agent.Current = Cabinet();
        await flow.PollAsync();
        agent.Handler = r => agent.Fail(r, 402, "Не хватает 50 ₽ на балансе");
        await flow.StartAsync(2);
        Assert.Equal("Не хватает 50 ₽ на балансе", flow.Error);
        Assert.Equal(Screen.Cabinet, flow.Screen);
    }

    [Fact]
    public async Task Logout_during_session_shows_the_server_hint()
    {
        var (flow, agent, _) = Create();
        agent.Current = Cabinet();
        await flow.PollAsync();
        agent.Handler = r => agent.Fail(r, 409, "Сначала завершите сессию");
        await flow.LogoutAsync();
        Assert.Equal("Сначала завершите сессию", flow.Error);
    }

    // ---------- служебное ----------
    [Fact]
    public async Task Keepalive_is_sent_only_when_the_guest_is_active_and_not_too_often()
    {
        var (flow, agent, time) = Create();
        agent.Current = Cabinet();
        await flow.PollAsync();
        time.Advance(TimeSpan.FromSeconds(50));
        await flow.PollAsync();
        Assert.Empty(agent.Ops(Ops.KeepAlive));          // гость ничего не трогал

        flow.Touch();
        await flow.PollAsync();
        Assert.Single(agent.Ops(Ops.KeepAlive));
        flow.Touch();
        time.Advance(TimeSpan.FromSeconds(20));
        await flow.PollAsync();
        Assert.Single(agent.Ops(Ops.KeepAlive));          // рано
        time.Advance(TimeSpan.FromSeconds(30));
        await flow.PollAsync();
        Assert.Equal(2, agent.Ops(Ops.KeepAlive).Count());
    }

    [Fact]
    public async Task Poll_is_skipped_while_an_action_is_running()
    {
        var gate = new TaskCompletionSource();
        var blocking = new BlockingAgent(new FakeAgent(), gate.Task);
        var flow = new FlowController(blocking) { Phone = "+7 900 000-00-01", Password = "x" };
        var login = flow.LoginAsync();
        await Task.Delay(50);
        Assert.True(flow.Busy);
        await flow.PollAsync();                          // не должен ничего отправить
        Assert.Single(blocking.Sent);
        gate.SetResult();
        await login;
        Assert.False(flow.Busy);
    }

    [Fact]
    public async Task Double_click_does_not_send_two_requests()
    {
        var gate = new TaskCompletionSource();
        var blocking = new BlockingAgent(new FakeAgent(), gate.Task);
        var flow = new FlowController(blocking) { Phone = "+7 900 000-00-01", Password = "x" };
        var first = flow.LoginAsync();
        var second = flow.LoginAsync();
        await Task.Delay(50);
        gate.SetResult();
        await Task.WhenAll(first, second);
        Assert.Single(blocking.Sent);
    }

    private sealed class BlockingAgent(FakeAgent inner, Task gate) : IAgentClient
    {
        public List<IpcRequest> Sent { get; } = [];
        public async Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken ct = default)
        {
            lock (Sent) Sent.Add(request);
            await gate;
            return await inner.SendAsync(request, ct);
        }
    }
}
