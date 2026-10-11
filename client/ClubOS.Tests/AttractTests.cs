using ClubOS.Service;
using ClubOS.Shared;
using ClubOS.Shared.Ui;
using Microsoft.Extensions.Time.Testing;

namespace ClubOS.Tests;

/// <summary>Заставка: баннер на весь экран, пока ПК простаивает; нажатие показывает форму входа; служба узнаёт, видна ли заставка.</summary>
public sealed class AttractTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clubos-attract-" + Guid.NewGuid().ToString("N"));

    public AttractTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* временная папка */ }
    }

    private static readonly AgentSnapshot Locked = new(true, PcMode.Locked, null, null, null, null, "Demo Club", "PC-1");
    private static readonly AgentSnapshot InCabinet = new(true, PcMode.Locked, new AccountInfo(2, "guest", "+7 *** ***-00-01", 500), null, null, null);

    private AdInfo Ad(int id) => new(id, "ООО Ромашка", "abc", 10, 1200, 400, $"{id:x8}", Path.Combine(_dir, $"{id}-{id:x8}.png"));

    private sealed record Rig(LauncherModel Launcher, AttractModel Attract, FlowController Flow, FakeAgent Agent, FakeTimeProvider Time, Func<IReadOnlyList<AdInfo>> Ads, Action<IReadOnlyList<AdInfo>> SetAds);

    private Rig Create(bool withAds = true)
    {
        var agent = new FakeAgent();
        IReadOnlyList<AdInfo> ads = withAds ? [Ad(1), Ad(2)] : [];
        agent.Handler = r => r.Op switch
        {
            Ops.Ads => new IpcResponse(r.Id, true, 200, null, AgentSnapshot.Booting, Ads: ads),
            Ops.Tariffs => agent.Ok(r, tariffs: []),
            Ops.Apps => agent.Ok(r, apps: []),
            _ => agent.Ok(r),
        };
        agent.Current = Locked;
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        var flow = new FlowController(agent, time);
        var launcher = new LauncherModel(flow, agent, new NoGames(), time, _dir);
        return new Rig(launcher, launcher.Attract, flow, agent, time, () => ads, v => ads = v);
    }

    private sealed class NoGames : IAppLauncher
    {
        public bool IsAvailable(AppInfo app) => true;
        public string? Launch(AppInfo app) => null;
    }

    private static async Task Poll(Rig r)
    {
        await r.Flow.PollAsync();
        await r.Launcher.PollAsync();
    }

    private static async Task Wait(Rig r, TimeSpan span)
    {
        r.Time.Advance(span);
        await Poll(r);
    }

    // ---------- когда заставка есть ----------
    [Fact]
    public async Task Without_ads_there_is_never_a_screensaver_and_the_form_stays()
    {
        var r = Create(withAds: false);
        await Poll(r);
        Assert.False(r.Attract.IsAttract);
        Assert.True(r.Attract.ShowForm);
        await Wait(r, TimeSpan.FromMinutes(10));
        Assert.False(r.Attract.IsAttract);
    }

    [Fact]
    public async Task An_idle_pc_with_ads_starts_with_the_screensaver_not_the_form()
    {
        var r = Create();
        Assert.False(r.Attract.IsAttract);                 // пока баннеры не получены
        await Poll(r);
        Assert.True(r.Attract.IsAttract);
        Assert.False(r.Attract.ShowForm);
    }

    [Fact]
    public async Task The_screensaver_does_not_jump_out_while_someone_is_typing_when_ads_arrive_late()
    {
        var r = Create(withAds: false);
        await Poll(r);
        r.SetAds([Ad(1)]);
        await Wait(r, TimeSpan.FromSeconds(58));
        r.Attract.Input(InputKind.Key);                    // гость сел и печатает
        await Wait(r, TimeSpan.FromSeconds(2));            // и в эту минуту баннеры как раз подгрузились (список обновляется раз в минуту)
        Assert.True(r.Launcher.Ads.HasAds);
        Assert.False(r.Attract.IsAttract);                 // заставка не должна выскочить поверх формы, пока ввод свежий
        await Wait(r, TimeSpan.FromSeconds(42));            // с последнего нажатия прошло 44 секунды
        Assert.False(r.Attract.IsAttract);
        await Wait(r, TimeSpan.FromSeconds(2));            // перестал трогать: через 45 секунд тишины заставка включается
        Assert.True(r.Attract.IsAttract);
    }

    // ---------- пробуждение ----------
    [Fact]
    public async Task A_click_or_a_key_wakes_the_form_and_is_consumed_so_it_does_not_hit_the_field_below()
    {
        foreach (var kind in new[] { InputKind.Click, InputKind.Key })
        {
            var r = Create();
            await Poll(r);
            Assert.True(r.Attract.IsAttract);
            Assert.True(r.Attract.Input(kind));            // разбудило: окно это нажатие дальше не пускает
            Assert.False(r.Attract.IsAttract);
            Assert.False(r.Attract.Input(kind));           // второе нажатие уже обычное: идёт в форму
        }
    }

    [Fact]
    public async Task Mouse_jitter_does_not_wake_but_a_real_move_does_and_a_move_is_not_consumed()
    {
        var r = Create();
        await Poll(r);
        Assert.False(r.Attract.Input(InputKind.Move, 500, 500));       // запомнили, где лежит мышь
        Assert.False(r.Attract.Input(InputKind.Move, 510, 495));       // дрожь на столе
        Assert.True(r.Attract.IsAttract);
        Assert.False(r.Attract.Input(InputKind.Move, 600, 560));       // реально сдвинули
        Assert.False(r.Attract.IsAttract);                             // форма открылась, но само движение ничего не «съело»
    }

    // ---------- возврат заставки ----------
    [Fact]
    public async Task The_empty_login_form_returns_to_the_screensaver_after_a_quiet_minute_and_any_input_postpones_it()
    {
        var r = Create();
        await Poll(r);
        r.Attract.Input(InputKind.Click);
        await Wait(r, TimeSpan.FromSeconds(40));
        Assert.False(r.Attract.IsAttract);                 // 45 секунд ещё не прошло
        r.Attract.Input(InputKind.Key);                    // гость что-то нажал
        await Wait(r, TimeSpan.FromSeconds(40));
        Assert.False(r.Attract.IsAttract);                 // отсчёт пошёл заново
        await Wait(r, TimeSpan.FromSeconds(10));
        Assert.True(r.Attract.IsAttract);
    }

    [Fact]
    public async Task A_form_with_typed_data_waits_longer_and_is_wiped_before_the_screensaver_returns()
    {
        var r = Create();
        await Poll(r);
        r.Attract.Input(InputKind.Click);
        r.Flow.Phone = "+79001234567";
        r.Flow.Password = "secret-password";
        await Wait(r, TimeSpan.FromSeconds(120));
        Assert.False(r.Attract.IsAttract);                 // что-то введено: ждём дольше
        Assert.Equal("+79001234567", r.Flow.Phone);
        await Wait(r, TimeSpan.FromSeconds(70));           // итого больше трёх минут тишины
        Assert.True(r.Attract.IsAttract);
        Assert.Equal("", r.Flow.Phone);                    // следующий гость чужой номер и пароль не увидит
        Assert.Equal("", r.Flow.Password);
        Assert.Equal(Screen.Login, r.Flow.Screen);
    }

    [Fact]
    public async Task A_long_registration_screen_is_reset_to_login_when_the_screensaver_returns()
    {
        var r = Create();
        await Poll(r);
        r.Attract.Input(InputKind.Click);
        r.Flow.GoTo(Screen.RegisterPhone);
        await Wait(r, TimeSpan.FromSeconds(100));
        Assert.False(r.Attract.IsAttract);
        await Wait(r, TimeSpan.FromSeconds(100));
        Assert.True(r.Attract.IsAttract);
        Assert.Equal(Screen.Login, r.Flow.Screen);
    }

    // ---------- кабинет, игра, выход ----------
    [Fact]
    public async Task A_guest_in_the_cabinet_never_sees_the_screensaver_and_it_returns_after_logout()
    {
        var r = Create();
        await Poll(r);
        r.Attract.Input(InputKind.Click);                  // гость сел и вошёл
        r.Agent.Current = InCabinet;
        await Wait(r, TimeSpan.FromMinutes(5));
        Assert.Equal(Screen.Cabinet, r.Flow.Screen);
        Assert.False(r.Attract.IsAttract);                 // в кабинете заставки нет, сколько бы он ни молчал

        r.Agent.Current = Locked;                          // вышел (или время кончилось и он ушёл)
        await Wait(r, TimeSpan.FromSeconds(10));
        Assert.True(r.Attract.IsAttract);                  // экран освободился: сразу заставка
    }

    [Fact]
    public async Task Right_after_the_guest_logs_out_by_hand_the_form_stays_a_little_so_the_screensaver_does_not_flash()
    {
        var r = Create();
        await Poll(r);
        r.Attract.Input(InputKind.Click);
        r.Agent.Current = InCabinet;
        await Wait(r, TimeSpan.FromSeconds(30));
        r.Attract.Input(InputKind.Click);                  // нажал «Выйти»
        r.Agent.Current = Locked;
        await Wait(r, TimeSpan.FromSeconds(1));
        Assert.False(r.Attract.IsAttract);                 // только что трогал мышь: форма на месте
        await Wait(r, TimeSpan.FromSeconds(50));
        Assert.True(r.Attract.IsAttract);                  // отошёл от ПК: заставка
    }

    [Fact]
    public async Task When_the_club_turns_ads_off_the_screensaver_goes_away()
    {
        var r = Create();
        await Poll(r);
        Assert.True(r.Attract.IsAttract);
        r.SetAds([]);
        await Wait(r, TimeSpan.FromMinutes(1));
        Assert.False(r.Attract.IsAttract);
    }

    // ---------- что сообщается службе ----------
    [Fact]
    public async Task The_service_is_told_when_the_screensaver_appears_disappears_and_every_few_seconds_in_between()
    {
        var r = Create();
        await Poll(r);
        var reports = () => r.Agent.Ops(Ops.AdsVisible).Select(x => x.Text).ToList();
        Assert.Equal(["1"], reports());                    // заставка включилась: сразу сообщили
        for (var i = 0; i < 4; i++) await Wait(r, TimeSpan.FromSeconds(1));
        Assert.Equal(["1"], reports());                    // чаще, чем раз в 5 секунд, не шлём
        await Wait(r, TimeSpan.FromSeconds(2));
        Assert.Equal(["1", "1"], reports());               // пульс
        r.Attract.Input(InputKind.Click);
        await Wait(r, TimeSpan.FromSeconds(1));
        Assert.Equal(["1", "1", "0"], reports());          // гость открыл форму: заставки нет, сказали сразу
    }

    [Fact]
    public async Task Without_ads_the_service_is_told_that_no_banner_is_on_screen()
    {
        var r = Create(withAds: false);
        await Poll(r);
        Assert.Equal(["0"], r.Agent.Ops(Ops.AdsVisible).Select(x => x.Text));
    }

    // ---------- служба: параметр для сервера ----------
    [Fact]
    public async Task The_service_passes_the_screensaver_state_to_the_server_so_only_real_screen_time_is_counted()
    {
        var (core, server, time) = Make.Core();
        server.On("GET", "/api/agent/state", 200, Samples.Locked);
        string LastQuery() => server.To("/api/agent/state").Last().Query;

        await core.RefreshAsync();
        Assert.Equal("", LastQuery());                                         // оверлей ещё ни разу не сообщал (старая версия): как раньше, без параметра

        await core.HandleAsync(new IpcRequest(1, Ops.AdsVisible, Text: "1"));
        await core.RefreshAsync();
        Assert.Equal("?ads=1", LastQuery());                                   // заставка на экране

        await core.HandleAsync(new IpcRequest(2, Ops.AdsVisible, Text: "0"));
        await core.RefreshAsync();
        Assert.Equal("?ads=0", LastQuery());                                   // гость у формы входа

        await core.HandleAsync(new IpcRequest(3, Ops.AdsVisible, Text: "1"));
        time.Advance(TimeSpan.FromSeconds(30));                                // оверлей замолчал (закрыт, завис): значит, баннера на экране нет
        await core.RefreshAsync();
        Assert.Equal("?ads=0", LastQuery());
    }

    [Fact]
    public async Task The_visibility_report_is_answered_locally_and_never_goes_to_the_server()
    {
        var (core, server, _) = Make.Core();
        var r = await core.HandleAsync(new IpcRequest(1, Ops.AdsVisible, Text: "1"));
        Assert.True(r.Ok);
        Assert.Empty(server.Requests);
    }
}
