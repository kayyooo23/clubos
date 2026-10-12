using ClubOS.Shared;
using ClubOS.Shared.Ui;
using Microsoft.Extensions.Time.Testing;

namespace ClubOS.Tests;

/// <summary>Сообщения администратора гостю: показываются один раз, доходят из ответа сервера до снимка, не показываются без связи и сотруднику.</summary>
public class NoticeTests
{
    private const string Quiet = "Пожалуйста, постарайтесь вести себя потише.";

    private static AgentSnapshot Snap(IReadOnlyList<NoticeInfo>? notices, bool connected = true, PcMode mode = PcMode.GuestSession) =>
        new(connected, mode, new AccountInfo(2, "guest", "+7 *** ***-00-01", 250), null, null, null, Notices: notices);

    private static NoticeInfo N(int id, string text = Quiet) => new(id, text);

    // ---------- когда показывать ----------
    [Fact]
    public void A_notice_is_shown_once_even_though_the_server_repeats_it()
    {
        var t = new NoticeTracker();
        Assert.Equal([1], t.Fresh(Snap([N(1)])).Select(n => n.Id));
        Assert.Empty(t.Fresh(Snap([N(1)])));                       // следующий опрос: то же сообщение, второй раз не показываем
        Assert.Empty(t.Fresh(Snap([N(1)])));
    }

    [Fact]
    public void A_new_notice_is_shown_next_to_one_that_was_already_shown()
    {
        var t = new NoticeTracker();
        t.Fresh(Snap([N(1)]));
        Assert.Equal([2], t.Fresh(Snap([N(1), N(2, "Подойдите к администратору.")])).Select(n => n.Id));
    }

    [Fact]
    public void Nothing_without_notices_or_with_an_empty_text()
    {
        var t = new NoticeTracker();
        Assert.Empty(t.Fresh(Snap(null)));
        Assert.Empty(t.Fresh(Snap([])));
        Assert.Empty(t.Fresh(Snap([N(5, "   ")])));
    }

    [Fact]
    public void A_notice_that_arrived_offline_is_shown_when_the_connection_returns()
    {
        var t = new NoticeTracker();
        Assert.Empty(t.Fresh(Snap([N(1)], connected: false)));     // без связи в снимке сообщений нет, но если бы были, показывать нельзя
        Assert.Equal([1], t.Fresh(Snap([N(1)])).Select(n => n.Id));
    }

    [Fact]
    public void A_staff_desktop_never_gets_a_notice()
    {
        var t = new NoticeTracker();
        Assert.Empty(t.Fresh(Snap([N(1)], mode: PcMode.StaffDesktop)));
    }

    [Fact]
    public void A_notice_is_shown_on_the_lock_screen_and_in_the_cabinet_too()
    {
        var t = new NoticeTracker();
        Assert.Single(t.Fresh(Snap([N(1)], mode: PcMode.Locked)));
    }

    [Fact]
    public void Several_new_notices_become_one_text()
    {
        var t = new NoticeTracker();
        var fresh = t.Fresh(Snap([N(1, "Первое."), N(2, " Второе. ")]));
        Assert.Equal("Первое.\n\nВторое.", NoticeTracker.Combine(fresh));
    }

    [Fact]
    public void Old_ids_are_forgotten_so_the_memory_does_not_grow()
    {
        var t = new NoticeTracker();
        for (var i = 1; i <= 150; i++) t.Fresh(Snap([N(i)]));
        Assert.Single(t.Fresh(Snap([N(1)])));                      // самый старый номер забыт: если сервер вдруг пришлёт его снова, покажем
        Assert.Empty(t.Fresh(Snap([N(150)])));
    }

    // ---------- дорога от сервера до экрана ----------
    [Fact]
    public async Task The_service_passes_notices_from_the_server_state_into_the_snapshot()
    {
        var (core, server, _) = Make.Core();
        server.On("GET", "/api/agent/state", 200,
            """{"mode":"locked","account":null,"session":null,"notices":[{"id":7,"text":"Пожалуйста, постарайтесь вести себя потише."}]}""");
        await core.RefreshAsync();
        var s = core.Snapshot();
        Assert.Equal([new NoticeInfo(7, Quiet)], s.Notices);
    }

    [Fact]
    public async Task Without_notices_the_snapshot_has_none_and_an_outage_does_not_replay_them()
    {
        var (core, server, _) = Make.Core();
        server.On("GET", "/api/agent/state", 200, """{"mode":"locked","account":null,"session":null,"notices":[{"id":7,"text":"Тише."}]}""");
        await core.RefreshAsync();
        server.Down = true;
        await core.RefreshAsync();
        Assert.Null(core.Snapshot().Notices);                      // связи нет: сообщение второй раз не нужно
    }

    // ---------- сообщение сервера на экране блокировки ----------
    [Fact]
    public async Task The_booked_message_from_the_server_is_shown_on_the_lock_screen_while_connected()
    {
        var agent = new FakeAgent();
        agent.Handler = r => agent.Ok(r);
        agent.Current = new AgentSnapshot(true, PcMode.Locked, null, null, "ПК забронирован на 19:30", null);
        var flow = new FlowController(agent, new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero)));
        await flow.PollAsync();
        Assert.Equal("ПК забронирован на 19:30", flow.Banner);
        Assert.False(flow.IsOffline);
    }

    [Fact]
    public async Task Without_a_server_message_there_is_no_banner_while_connected()
    {
        var agent = new FakeAgent();
        agent.Handler = r => agent.Ok(r);
        agent.Current = new AgentSnapshot(true, PcMode.Locked, null, null, null, null);
        var flow = new FlowController(agent, new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero)));
        await flow.PollAsync();
        Assert.Null(flow.Banner);
    }
}
