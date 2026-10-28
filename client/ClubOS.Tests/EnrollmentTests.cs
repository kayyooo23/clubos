using ClubOS.Service;
using ClubOS.Shared;
using Microsoft.Extensions.Time.Testing;

namespace ClubOS.Tests;

/// <summary>Подключение ПК по коду: откуда служба берёт ключ, как ведёт себя при сбоях и что делает, когда сервер отверг ключ.</summary>
public class EnrollmentTests
{
    private const string Machine = "0123456789abcdef0123456789abcdef";
    private const string Enrolled = """{"pc_id":7,"pc_name":"DESKTOP-AB12","club_name":"Demo Club","zone":"VIP","key":"key-from-server-111"}""";

    private sealed class Rig
    {
        public required ServerApi Api;
        public required FakeServer Server;
        public required FakeTimeProvider Time;
        public required MemoryIdentityStore Store;
        public required EnrollmentManager Manager;
        public required List<string> Log;
    }

    private static Rig Create(string? code = "K7M2-9QXA-4TPD", string? pcKey = null, PcIdentity? saved = null)
    {
        var server = new FakeServer();
        var api = new ServerApi(new HttpClient(server) { BaseAddress = new Uri("http://club.test") }, null, Machine);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));
        var store = new MemoryIdentityStore();
        if (saved is not null) store.Save(saved);
        var log = new List<string>();
        var manager = new EnrollmentManager(api, new AgentConfig("http://club.test", pcKey, EnrollCode: code), store, Machine, "DESKTOP-AB12", time, log.Add);
        return new Rig { Api = api, Server = server, Time = time, Store = store, Manager = manager, Log = log };
    }

    // ---------- откуда ключ ----------
    [Fact]
    public async Task Enrolls_with_the_code_and_remembers_the_key()
    {
        var rig = Create();
        rig.Server.On("POST", "/api/agent/enroll", 200, Enrolled);
        Assert.True(await rig.Manager.EnsureAsync());
        var sent = Assert.Single(rig.Server.To("/api/agent/enroll"));
        Assert.Contains("\"code\":\"K7M2-9QXA-4TPD\"", sent.Body);
        Assert.Contains($"\"machine_id\":\"{Machine}\"", sent.Body);
        Assert.Contains("\"hostname\":\"DESKTOP-AB12\"", sent.Body);
        Assert.Null(sent.PcKey);                                          // ключа ещё нет, чужой не отправляем
        Assert.True(rig.Api.HasKey);
        Assert.Equal("key-from-server-111", rig.Store.Value!.Key);
        Assert.Equal("DESKTOP-AB12", rig.Manager.Identity!.PcName);
        Assert.Null(rig.Manager.Problem);
        Assert.Contains(rig.Log, m => m.Contains("Demo Club"));
    }

    [Fact]
    public async Task After_enrolling_every_request_carries_the_new_key_and_the_machine_fingerprint()
    {
        var rig = Create();
        rig.Server.On("POST", "/api/agent/enroll", 200, Enrolled).On("GET", "/api/agent/state", 200, Samples.Locked);
        await rig.Manager.EnsureAsync();
        await rig.Api.GetAsync<ServerState>("/api/agent/state");
        var seen = rig.Server.To("/api/agent/state").Single();
        Assert.Equal("key-from-server-111", seen.PcKey);
        Assert.Equal(Machine, seen.MachineId);
    }

    [Fact]
    public async Task A_key_written_into_the_settings_wins_and_no_enrollment_happens()
    {
        var rig = Create(pcKey: "manual-key", saved: new PcIdentity("stored-key"));
        Assert.True(await rig.Manager.EnsureAsync());
        Assert.Empty(rig.Server.Requests);
        rig.Server.On("GET", "/api/agent/state", 200, Samples.Locked);
        await rig.Api.GetAsync<ServerState>("/api/agent/state");
        Assert.Equal("manual-key", rig.Server.Requests[0].PcKey);
    }

    [Fact]
    public async Task The_saved_key_is_used_after_a_restart_without_asking_the_server()
    {
        var rig = Create(saved: new PcIdentity("stored-key", 7, "PC-7", "Demo Club"));
        Assert.True(await rig.Manager.EnsureAsync());
        Assert.Empty(rig.Server.Requests);
        Assert.True(rig.Api.HasKey);
        Assert.Equal("PC-7", rig.Manager.Identity!.PcName);
    }

    [Fact]
    public async Task Without_a_key_and_a_code_the_pc_says_what_is_missing_and_does_not_call_the_server()
    {
        var rig = Create(code: null);
        Assert.False(await rig.Manager.EnsureAsync());
        Assert.Contains("кода подключения", rig.Manager.Problem);
        Assert.Empty(rig.Server.Requests);
    }

    // ---------- сбои ----------
    [Fact]
    public async Task A_lost_connection_is_retried_soon_with_growing_pauses()
    {
        var rig = Create();
        rig.Server.Down = true;
        Assert.False(await rig.Manager.EnsureAsync());
        Assert.Equal("Нет связи с сервером", rig.Manager.Problem);
        Assert.False(await rig.Manager.EnsureAsync());                    // пауза ещё не прошла: на сервер не ходим
        Assert.Equal(1, rig.Server.Attempts);
        rig.Time.Advance(EnrollmentManager.NetworkBackoff[0] + TimeSpan.FromSeconds(1));
        await rig.Manager.EnsureAsync();
        Assert.Equal(2, rig.Server.Attempts);
        rig.Time.Advance(EnrollmentManager.NetworkBackoff[0] + TimeSpan.FromSeconds(1));     // вторая пауза длиннее первой
        await rig.Manager.EnsureAsync();
        Assert.Equal(2, rig.Server.Attempts);
        rig.Time.Advance(EnrollmentManager.NetworkBackoff[1]);
        rig.Server.Down = false;
        rig.Server.On("POST", "/api/agent/enroll", 200, Enrolled);
        Assert.True(await rig.Manager.EnsureAsync());
        Assert.Equal(3, rig.Server.Attempts);
    }

    [Theory]
    [InlineData(403, "Код подключения недействителен: он неверный, истёк, отозван или уже использован")]
    [InlineData(429, "Слишком много неверных попыток, повторите позже")]
    public async Task A_refused_code_is_not_retried_for_ten_minutes_and_tells_the_admin_to_act(int status, string detail)
    {
        var rig = Create();
        rig.Server.On("POST", "/api/agent/enroll", status, Samples.Error(detail));
        Assert.False(await rig.Manager.EnsureAsync());
        Assert.Contains(detail, rig.Manager.Problem);
        Assert.Contains("администратору", rig.Manager.Problem);
        Assert.False(await rig.Manager.EnsureAsync());
        rig.Time.Advance(EnrollmentManager.CodeRetry - TimeSpan.FromSeconds(1));
        await rig.Manager.EnsureAsync();
        Assert.Single(rig.Server.Requests);                               // ещё рано: тридцать ПК за одним адресом не должны исчерпать лимит попыток
        rig.Time.Advance(TimeSpan.FromSeconds(2));
        await rig.Manager.EnsureAsync();
        Assert.Equal(2, rig.Server.Requests.Count);
    }

    // ---------- сервер отверг ключ ----------
    [Theory]
    [InlineData(401, null)]
    [InlineData(409, "machine_mismatch")]
    public async Task A_rejected_enrolled_key_is_forgotten_and_the_pc_enrolls_again(int status, string? code)
    {
        var rig = Create();
        rig.Server.On("POST", "/api/agent/enroll", 200, Enrolled);
        await rig.Manager.EnsureAsync();
        var message = rig.Manager.KeyRejected(status, code);
        Assert.Equal("Переподключение к клубу…", message);
        Assert.False(rig.Api.HasKey);
        Assert.Null(rig.Store.Value);
        rig.Server.On("POST", "/api/agent/enroll", 200, """{"pc_id":8,"pc_name":"DESKTOP-AB12-2","club_name":"Demo Club","zone":null,"key":"second-key-222"}""");
        Assert.True(await rig.Manager.EnsureAsync());                     // не ждём паузу: ключ только что был рабочим
        Assert.Equal("second-key-222", rig.Store.Value!.Key);
        Assert.Equal(2, rig.Server.To("/api/agent/enroll").Count());
    }

    [Fact]
    public async Task A_rejected_saved_key_after_restart_is_replaced_the_same_way()
    {
        var rig = Create(saved: new PcIdentity("old-key"));
        await rig.Manager.EnsureAsync();
        Assert.Equal("Переподключение к клубу…", rig.Manager.KeyRejected(401, null));
        Assert.Null(rig.Store.Value);
        Assert.False(rig.Api.HasKey);
    }

    [Fact]
    public async Task A_rejected_manual_key_is_not_replaced_and_the_message_says_who_to_call()
    {
        var rig = Create(pcKey: "manual-key");
        await rig.Manager.EnsureAsync();
        Assert.Contains("недействителен", rig.Manager.KeyRejected(401, null));
        Assert.Contains("другому компьютеру", rig.Manager.KeyRejected(409, "machine_mismatch"));
        Assert.True(rig.Api.HasKey);                                      // ключ вписан руками: подменять его самим подключением нельзя
    }

    [Fact]
    public async Task An_ordinary_error_is_not_a_key_problem()
    {
        var rig = Create();
        rig.Server.On("POST", "/api/agent/enroll", 200, Enrolled);
        await rig.Manager.EnsureAsync();
        Assert.Null(rig.Manager.KeyRejected(500, null));
        Assert.Null(rig.Manager.KeyRejected(409, "something_else"));
        Assert.True(rig.Api.HasKey);
        Assert.NotNull(rig.Store.Value);
    }

    // ---------- вместе с ядром службы ----------
    private static (AgentCore Core, Rig Rig) CoreWith(Rig rig)
    {
        var temp = Path.Combine(Path.GetTempPath(), "clubos-test-enroll-" + Guid.NewGuid().ToString("N"));
        var cfg = new AgentConfig("http://club.test", null, EnrollCode: "K7M2-9QXA-4TPD");
        var core = new AgentCore(rig.Api, cfg, rig.Time, null, new AdImageCache(rig.Api, Path.Combine(temp, "ads")),
            new CoverCache(rig.Api, Path.Combine(temp, "covers")), rig.Manager);
        return (core, rig);
    }

    [Fact]
    public async Task The_lock_screen_explains_why_a_pc_without_a_key_is_not_connected()
    {
        var (core, rig) = CoreWith(Create());
        rig.Server.On("POST", "/api/agent/enroll", 403, Samples.Error("Код подключения недействителен"));
        await core.RefreshAsync();
        var snapshot = core.Snapshot();
        Assert.False(snapshot.Connected);
        Assert.Contains("Код подключения недействителен", snapshot.Message);
        Assert.Empty(rig.Server.To("/api/agent/state"));                  // без ключа состояние не запрашиваем
    }

    [Fact]
    public async Task The_core_enrolls_first_then_reads_the_state_with_the_new_key()
    {
        var (core, rig) = CoreWith(Create());
        rig.Server.On("POST", "/api/agent/enroll", 200, Enrolled).On("GET", "/api/agent/state", 200,
            """{"mode":"locked","account":null,"session":null,"club_name":"Demo Club","pc_name":"DESKTOP-AB12"}""");
        await core.RefreshAsync();
        Assert.True(core.Snapshot().Connected);
        Assert.Equal("key-from-server-111", rig.Server.To("/api/agent/state").Single().PcKey);
    }

    [Fact]
    public async Task A_clone_is_told_apart_and_enrolls_as_a_new_pc_on_the_next_round()
    {
        var (core, rig) = CoreWith(Create(saved: new PcIdentity("cloned-key")));
        rig.Server.On("GET", "/api/agent/state", 409, """{"detail":"Этот ключ ПК привязан к другому компьютеру","code":"machine_mismatch"}""");
        await core.RefreshAsync();
        Assert.False(core.Snapshot().Connected);
        Assert.Equal("Переподключение к клубу…", core.Snapshot().Message);
        Assert.Null(rig.Store.Value);
        rig.Server.On("POST", "/api/agent/enroll", 200, Enrolled).On("GET", "/api/agent/state", 200, Samples.Locked);
        await core.RefreshAsync();
        Assert.True(core.Snapshot().Connected);
        Assert.Equal("key-from-server-111", rig.Server.To("/api/agent/state").Last().PcKey);
    }

    [Fact]
    public async Task Commands_from_the_server_are_handed_over_once()
    {
        var (core, rig) = CoreWith(Create(saved: new PcIdentity("k")));
        rig.Server.On("GET", "/api/agent/state", 200,
            """{"mode":"locked","account":null,"session":null,"commands":[{"id":5,"kind":"reboot","payload":{"force":true}},{"id":6,"kind":"shutdown","payload":{"force":false}}]}""");
        await core.RefreshAsync();
        var taken = core.TakeCommands();
        Assert.Equal([5, 6], taken.Select(c => c.Id));
        Assert.Equal("reboot", taken[0].Kind);
        Assert.True(taken[0].Payload!.Force);
        Assert.Empty(core.TakeCommands());                                // повторно не отдаются
        rig.Server.On("GET", "/api/agent/state", 200, Samples.Locked);
        await core.RefreshAsync();
        Assert.Empty(core.TakeCommands());
    }
}
