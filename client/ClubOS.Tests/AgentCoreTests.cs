using ClubOS.Service;
using System.Text.Json;
using ClubOS.Shared;

namespace ClubOS.Tests;

public class AgentCoreTests
{
    private static IpcRequest Req(string op, string? phone = null, string? password = null, string? code = null,
        bool terms = false, int tariff = 0) => new(1, op, phone, password, code, terms, tariff);

    // ---------- состояние и офлайн ----------
    [Fact]
    public void Starts_locked_and_disconnected_before_the_first_answer()
    {
        var (core, _, _) = Make.Core();
        var s = core.Snapshot();
        Assert.False(s.Connected);
        Assert.Equal(PcMode.Locked, s.Mode);
    }

    [Fact]
    public async Task Refresh_publishes_server_state()
    {
        var store = new MemoryTokenStore();
        store.Save("saved-token");                       // гость уже вошёл, служба помнит его токен
        var (core, server, _) = Make.Core(store: store);
        server.On("GET", "/api/agent/state", 200, Samples.Cabinet);
        await core.RefreshAsync();
        var s = core.Snapshot();
        Assert.True(s.Connected);
        Assert.Equal(500, s.Account!.Balance);
        Assert.Equal(FakeServer.PcKey, server.Requests[0].PcKey);
    }

    [Fact]
    public async Task Club_and_pc_names_survive_an_outage()
    {
        var (core, server, time) = Make.Core();
        server.On("GET", "/api/agent/state", 200, """{"mode":"locked","account":null,"session":null,"club_name":"Demo Club","pc_name":"PC-3"}""");
        await core.RefreshAsync();
        Assert.Equal(("Demo Club", "PC-3"), (core.Snapshot().ClubName, core.Snapshot().PcName));
        server.Down = true;
        await core.RefreshAsync();
        time.Advance(TimeSpan.FromMinutes(10));
        var offline = core.Snapshot();
        Assert.False(offline.Connected);
        Assert.Equal(("Demo Club", "PC-3"), (offline.ClubName, offline.PcName));     // экран не теряет название без связи
    }

    [Fact]
    public async Task Countdown_runs_locally_between_polls()
    {
        var (core, server, time) = Make.Core();
        server.On("GET", "/api/agent/state", 200, Samples.Playing);
        await core.RefreshAsync();
        Assert.Equal(10800, core.Snapshot().SecondsLeft);
        time.Advance(TimeSpan.FromSeconds(45));
        Assert.Equal(10755, core.Snapshot().SecondsLeft);
    }

    [Fact]
    public async Task Player_keeps_playing_through_a_short_outage_then_pc_locks()
    {
        var (core, server, time) = Make.Core(graceSeconds: 120);
        server.On("GET", "/api/agent/state", 200, Samples.Playing);
        await core.RefreshAsync();
        server.Down = true;
        await core.RefreshAsync();
        time.Advance(TimeSpan.FromSeconds(60));
        var short_ = core.Snapshot();
        Assert.False(short_.Connected);
        Assert.Equal(PcMode.GuestSession, short_.Mode);
        time.Advance(TimeSpan.FromSeconds(61));          // запас в 120 с вышел
        Assert.Equal(PcMode.Locked, core.Snapshot().Mode);
        Assert.Null(core.Snapshot().Session);
    }

    [Fact]
    public async Task Package_time_running_out_offline_locks_even_inside_the_grace_period()
    {
        var (core, server, time) = Make.Core(graceSeconds: 600);
        server.On("GET", "/api/agent/state", 200, Samples.Playing.Replace("\"seconds_left\":10800", "\"seconds_left\":30"));
        await core.RefreshAsync();
        server.Down = true;
        await core.RefreshAsync();
        time.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(PcMode.GuestSession, core.Snapshot().Mode);
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(PcMode.Locked, core.Snapshot().Mode);
    }

    [Fact]
    public async Task Reconnect_restores_the_real_state()
    {
        var (core, server, time) = Make.Core();
        server.On("GET", "/api/agent/state", 200, Samples.Playing);
        await core.RefreshAsync();
        server.Down = true;
        await core.RefreshAsync();
        time.Advance(TimeSpan.FromSeconds(500));
        Assert.Equal(PcMode.Locked, core.Snapshot().Mode);
        server.Down = false;
        await core.RefreshAsync();
        Assert.Equal(PcMode.GuestSession, core.Snapshot().Mode);
        Assert.True(core.Snapshot().Connected);
    }

    [Fact]
    public async Task Staff_desktop_survives_an_outage_and_a_locked_pc_stays_locked()
    {
        var (core, server, time) = Make.Core();
        server.On("GET", "/api/agent/state", 200, Samples.Staff);
        await core.RefreshAsync();
        server.Down = true;
        await core.RefreshAsync();
        time.Advance(TimeSpan.FromHours(3));
        Assert.Equal(PcMode.StaffDesktop, core.Snapshot().Mode);

        var (core2, server2, _) = Make.Core();
        server2.On("GET", "/api/agent/state", 200, Samples.Locked);
        await core2.RefreshAsync();
        server2.Down = true;
        await core2.RefreshAsync();
        Assert.Equal(PcMode.Locked, core2.Snapshot().Mode);
        Assert.False(core2.Snapshot().Connected);
    }

    [Fact]
    public async Task Revoked_pc_key_is_reported_clearly_and_keeps_pc_locked()
    {
        var (core, server, _) = Make.Core();
        server.On("GET", "/api/agent/state", 401, Samples.Error("Неверный ключ ПК"));
        await core.RefreshAsync();
        var s = core.Snapshot();
        Assert.Equal(PcMode.Locked, s.Mode);
        Assert.Contains("Ключ этого ПК недействителен", s.Message);
    }

    // ---------- вход и токен ----------
    private static (AgentCore Core, FakeServer Server) LoggedIn()
    {
        var (core, server, _) = Make.Core();
        server.On("GET", "/api/agent/state", 200, Samples.Cabinet)
              .On("POST", "/api/auth/login", 200, Samples.LoginOk);
        return (core, server);
    }

    [Fact]
    public async Task Login_keeps_the_token_inside_the_service_and_never_returns_it()
    {
        var (core, server) = LoggedIn();
        var resp = await core.HandleAsync(Req(Ops.Login, "+79000000001", "secret-pass-1"));
        Assert.True(resp.Ok);
        Assert.Equal(500, resp.Snapshot!.Account!.Balance);
        var wire = JsonSerializer.Serialize(resp, Json.Options);
        Assert.DoesNotContain(FakeServer.GuestToken, wire);
        Assert.DoesNotContain(FakeServer.PcKey, wire);
        Assert.DoesNotContain("secret-pass-1", wire);
        using var body = JsonDocument.Parse(server.To("/api/auth/login").Single().Body);   // «+» в JSON экранируется, поэтому разбираем, а не ищем подстроку
        Assert.Equal("+79000000001", body.RootElement.GetProperty("phone").GetString());
    }

    [Fact]
    public async Task Later_calls_carry_the_token_that_login_returned()
    {
        var (core, server) = LoggedIn();
        server.On("POST", "/api/me/sessions", 200, "{}").On("POST", "/api/me/sessions/stop", 200, "{}");
        await core.HandleAsync(Req(Ops.Login, "+79000000001", "p"));
        var start = await core.HandleAsync(Req(Ops.StartSession, tariff: 2));
        Assert.True(start.Ok);
        Assert.Equal("Bearer " + FakeServer.GuestToken, server.To("/api/me/sessions").Single().Auth);
        Assert.Contains("\"tariff_id\":2", server.To("/api/me/sessions").Single().Body);
        await core.HandleAsync(Req(Ops.StopSession));
        Assert.Equal("Bearer " + FakeServer.GuestToken, server.To("/api/me/sessions/stop").Single().Auth);
    }

    [Fact]
    public async Task Wrong_password_is_reported_and_no_token_is_kept()
    {
        var (core, server, _) = Make.Core();
        server.On("GET", "/api/agent/state", 200, Samples.Locked)
              .On("POST", "/api/auth/login", 401, Samples.Error("Неверный номер или пароль"))
              .On("POST", "/api/me/sessions", 401, Samples.Error("Нужен вход"));
        var resp = await core.HandleAsync(Req(Ops.Login, "+79000000001", "bad"));
        Assert.False(resp.Ok);
        Assert.Equal(401, resp.Status);
        Assert.Equal("Неверный номер или пароль", resp.Error);
        await core.HandleAsync(Req(Ops.StartSession, tariff: 1));
        Assert.Null(server.To("/api/me/sessions").Single().Auth);
    }

    [Fact]
    public async Task Server_forgetting_the_login_makes_the_service_forget_the_token()
    {
        var (core, server) = LoggedIn();
        server.On("POST", "/api/me/sessions", 200, "{}");
        await core.HandleAsync(Req(Ops.Login, "+79000000001", "p"));
        server.On("GET", "/api/agent/state", 200, Samples.Locked);   // сервер вышел по бездействию
        await core.RefreshAsync();
        await core.HandleAsync(Req(Ops.StartSession, tariff: 1));
        Assert.Null(server.To("/api/me/sessions").Single().Auth);
    }

    [Fact]
    public async Task Expired_token_on_an_action_returns_to_the_login_screen()
    {
        var (core, server) = LoggedIn();
        server.On("POST", "/api/me/sessions", 401, Samples.Error("Вход недействителен, войдите заново"));
        await core.HandleAsync(Req(Ops.Login, "+79000000001", "p"));
        server.On("GET", "/api/agent/state", 200, Samples.Locked);
        var resp = await core.HandleAsync(Req(Ops.StartSession, tariff: 1));
        Assert.False(resp.Ok);
        Assert.Null(resp.Snapshot!.Account);
    }

    [Fact]
    public async Task Registration_confirm_logs_the_guest_in()
    {
        var (core, server, _) = Make.Core();
        server.On("POST", "/api/auth/register/request-code", 200, Samples.Resend)
              .On("POST", "/api/auth/register/confirm", 200, Samples.RegisterOk)
              .On("GET", "/api/agent/state", 200, Samples.Cabinet)
              .On("POST", "/api/me/sessions", 200, "{}");
        var code = await core.HandleAsync(Req(Ops.RegisterRequestCode, "+79000000001"));
        Assert.True(code.Ok);
        Assert.Equal(60, code.ResendAfter);
        var confirm = await core.HandleAsync(Req(Ops.RegisterConfirm, "+79000000001", "guest-pass-123", "123456", true));
        Assert.True(confirm.Ok);
        var body = server.To("/api/auth/register/confirm").Single().Body;
        Assert.Contains("\"accept_terms\":true", body);
        Assert.Contains("\"code\":\"123456\"", body);
        await core.HandleAsync(Req(Ops.StartSession, tariff: 1));
        Assert.Equal("Bearer " + FakeServer.GuestToken, server.To("/api/me/sessions").Single().Auth);
    }

    [Fact]
    public async Task Password_reset_does_not_log_in()
    {
        var (core, server, _) = Make.Core();
        server.On("POST", "/api/auth/password-reset/request-code", 200, Samples.Resend)
              .On("POST", "/api/auth/password-reset/confirm", 200, Samples.Ok)
              .On("GET", "/api/agent/state", 200, Samples.Locked)
              .On("POST", "/api/me/sessions", 401, Samples.Error("Нужен вход"));
        Assert.True((await core.HandleAsync(Req(Ops.ResetRequestCode, "+79000000001"))).Ok);
        Assert.True((await core.HandleAsync(Req(Ops.ResetConfirm, "+79000000001", "new-pass-123", "123456"))).Ok);
        var body = server.To("/api/auth/password-reset/confirm").Single().Body;
        Assert.Contains("\"new_password\":\"new-pass-123\"", body);
        await core.HandleAsync(Req(Ops.StartSession, tariff: 1));
        Assert.Null(server.To("/api/me/sessions").Single().Auth);
    }

    [Fact]
    public async Task Logout_clears_the_token_but_waits_for_the_session_to_end()
    {
        var (core, server) = LoggedIn();
        await core.HandleAsync(Req(Ops.Login, "+79000000001", "p"));
        server.On("POST", "/api/auth/logout", 409, Samples.Error("Сначала завершите сессию"));
        var blocked = await core.HandleAsync(Req(Ops.Logout));
        Assert.False(blocked.Ok);
        Assert.Equal("Сначала завершите сессию", blocked.Error);

        server.On("POST", "/api/auth/logout", 200, Samples.Ok).On("GET", "/api/agent/state", 200, Samples.Locked)
              .On("POST", "/api/me/sessions", 401, Samples.Error("Нужен вход"));
        Assert.True((await core.HandleAsync(Req(Ops.Logout))).Ok);
        await core.HandleAsync(Req(Ops.StartSession, tariff: 1));
        Assert.Null(server.To("/api/me/sessions").Single().Auth);
    }

    [Fact]
    public async Task Logout_without_connection_reports_it_and_keeps_the_login()
    {
        var (core, server) = LoggedIn();
        await core.HandleAsync(Req(Ops.Login, "+79000000001", "p"));
        server.Down = true;
        var resp = await core.HandleAsync(Req(Ops.Logout));
        Assert.False(resp.Ok);
        Assert.Equal(0, resp.Status);
    }

    [Fact]
    public async Task Keepalive_pings_the_server_only_for_a_logged_in_guest()
    {
        var (core, server, _) = Make.Core();
        server.On("GET", "/api/agent/state", 200, Samples.Locked).On("GET", "/api/me", 200, "{}");
        await core.RefreshAsync();
        await core.HandleAsync(Req(Ops.KeepAlive));
        Assert.Empty(server.To("/api/me"));                    // не вошёл — пинговать нечего

        server.On("GET", "/api/agent/state", 200, Samples.Cabinet).On("POST", "/api/auth/login", 200, Samples.LoginOk);
        await core.HandleAsync(Req(Ops.Login, "+79000000001", "p"));
        await core.HandleAsync(Req(Ops.KeepAlive));
        Assert.Equal("Bearer " + FakeServer.GuestToken, server.To("/api/me").Single().Auth);
    }

    [Fact]
    public async Task Keepalive_401_drops_the_login()
    {
        var (core, server) = LoggedIn();
        await core.HandleAsync(Req(Ops.Login, "+79000000001", "p"));
        server.On("GET", "/api/me", 401, Samples.Error("Вход недействителен")).On("GET", "/api/agent/state", 200, Samples.Locked);
        var resp = await core.HandleAsync(Req(Ops.KeepAlive));
        Assert.Null(resp.Snapshot!.Account);
    }

    [Fact]
    public async Task Tariffs_and_transactions_are_returned()
    {
        var (core, server) = LoggedIn();
        server.On("GET", "/api/agent/tariffs", 200, Samples.Tariffs).On("GET", "/api/me/transactions", 200, Samples.Transactions);
        await core.HandleAsync(Req(Ops.Login, "+79000000001", "p"));
        Assert.Equal(3, (await core.HandleAsync(Req(Ops.Tariffs))).Tariffs!.Count);
        Assert.Equal(2, (await core.HandleAsync(Req(Ops.Transactions))).Transactions!.Count);
    }

    [Fact]
    public async Task Apps_catalog_is_read_with_the_pc_key_only_and_parsed()
    {
        var (core, server) = LoggedIn();
        server.On("GET", "/api/agent/apps", 200, Samples.Apps);
        var resp = await core.HandleAsync(Req(Ops.Apps));
        Assert.True(resp.Ok);
        Assert.Equal(["Dota 2", "Chrome"], resp.Apps!.Select(a => a.Title));
        Assert.Equal("steam", resp.Apps![0].LaunchKind);
        Assert.Equal("570", resp.Apps[0].LaunchTarget);
        Assert.Equal(12, resp.Apps[0].AgeRating);
        Assert.Null(resp.Apps[1].AgeRating);
        var seen = server.To("/api/agent/apps").Single();
        Assert.Equal(FakeServer.PcKey, seen.PcKey);
        Assert.Null(seen.Auth);                       // каталог не секрет: токен гостя в этом запросе не нужен
    }

    [Fact]
    public async Task Apps_failure_is_reported_without_a_crash()
    {
        var (core, server) = LoggedIn();
        server.Down = true;
        var resp = await core.HandleAsync(Req(Ops.Apps));
        Assert.False(resp.Ok);
        Assert.Null(resp.Apps);
    }

    // ---------- защита от оверлея и мусора ----------
    [Theory]
    [InlineData("rm -rf")]
    [InlineData("")]
    [InlineData("/api/admin/pcs")]
    [InlineData("GET http://evil/")]
    public async Task Unknown_operations_are_refused_without_touching_the_server(string op)
    {
        var (core, server, _) = Make.Core();
        var resp = await core.HandleAsync(Req(op));
        Assert.False(resp.Ok);
        Assert.Equal(400, resp.Status);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Oversized_fields_are_refused()
    {
        var (core, server, _) = Make.Core();
        var resp = await core.HandleAsync(Req(Ops.Login, new string('9', 500), "p"));
        Assert.False(resp.Ok);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task State_op_never_calls_the_server()
    {
        var (core, server, _) = Make.Core();
        for (var i = 0; i < 5; i++) Assert.True((await core.HandleAsync(Req(Ops.State))).Ok);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Actions_are_serialized_so_login_and_logout_cannot_interleave()
    {
        var (core, server, _) = Make.Core();
        var active = 0; var maxActive = 0;
        server.On("GET", "/api/agent/state", 200, Samples.Locked);
        server.On("POST", "/api/auth/login", _ =>
        {
            var now = Interlocked.Increment(ref active);
            maxActive = Math.Max(maxActive, now);
            Thread.Sleep(30);
            Interlocked.Decrement(ref active);
            return (200, Samples.LoginOk);
        });
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => core.HandleAsync(Req(Ops.Login, "+79000000001", "p"))));
        Assert.Equal(1, maxActive);
    }
}


public class TokenPersistenceTests
{
    [Fact]
    public async Task Login_survives_a_service_restart()
    {
        var store = new MemoryTokenStore();
        var (first, server, _) = Make.Core(store: store);
        server.On("GET", "/api/agent/state", 200, Samples.Cabinet).On("POST", "/api/auth/login", 200, Samples.LoginOk)
              .On("POST", "/api/me/sessions", 200, "{}");
        await first.HandleAsync(new(1, Ops.Login, "+79000000001", "p"));
        Assert.Equal(FakeServer.GuestToken, store.Value);

        var (second, server2, _) = Make.Core(store: store);       // «служба перезапустилась»
        server2.On("GET", "/api/agent/state", 200, Samples.Cabinet).On("POST", "/api/me/sessions", 200, "{}");
        await second.RefreshAsync();
        Assert.Equal(500, second.Snapshot().Account!.Balance);     // кабинет на месте
        await second.HandleAsync(new(2, Ops.StartSession, TariffId: 2));
        Assert.Equal("Bearer " + FakeServer.GuestToken, server2.To("/api/me/sessions").Single().Auth);
    }

    [Fact]
    public async Task Without_a_token_the_cabinet_is_hidden_so_the_guest_logs_in_again()
    {
        var (core, server, _) = Make.Core();                        // токена нет, а сервер считает гостя вошедшим
        server.On("GET", "/api/agent/state", 200, Samples.Cabinet);
        await core.RefreshAsync();
        var s = core.Snapshot();
        Assert.True(s.Connected);
        Assert.Equal(PcMode.Locked, s.Mode);
        Assert.Null(s.Account);

        server.On("GET", "/api/agent/state", 200, Samples.Playing);  // а играющему гостю таймер и баланс остаются
        await core.RefreshAsync();
        Assert.Equal(250, core.Snapshot().Account!.Balance);
    }

    [Fact]
    public async Task Server_ending_the_login_wipes_the_stored_token()
    {
        var store = new MemoryTokenStore();
        store.Save("old-token");
        var (core, server, _) = Make.Core(store: store);
        server.On("GET", "/api/agent/state", 200, Samples.Locked);
        await core.RefreshAsync();
        Assert.Null(store.Value);
    }

    [Fact]
    public async Task Logout_wipes_the_stored_token()
    {
        var store = new MemoryTokenStore();
        var (core, server, _) = Make.Core(store: store);
        server.On("GET", "/api/agent/state", 200, Samples.Cabinet).On("POST", "/api/auth/login", 200, Samples.LoginOk)
              .On("POST", "/api/auth/logout", 200, Samples.Ok);
        await core.HandleAsync(new(1, Ops.Login, "+79000000001", "p"));
        Assert.NotNull(store.Value);
        server.On("GET", "/api/agent/state", 200, Samples.Locked);
        await core.HandleAsync(new(2, Ops.Logout));
        Assert.Null(store.Value);
    }
}

public class DpapiTokenStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clubos-test-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "token.bin");
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Round_trip_and_the_file_is_not_plain_text()
    {
        var store = new DpapiTokenStore(File_);
        store.Save("super-secret-token-123");
        Assert.DoesNotContain("super-secret-token-123", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(File_)));
        Assert.Equal("super-secret-token-123", new DpapiTokenStore(File_).Load());
    }

    [Fact]
    public void Clearing_deletes_the_file_and_missing_file_is_fine()
    {
        var store = new DpapiTokenStore(File_);
        Assert.Null(store.Load());
        store.Save("t");
        store.Save(null);
        Assert.False(File.Exists(File_));
        Assert.Null(store.Load());
    }

    [Fact]
    public void Corrupt_file_is_ignored_with_a_warning()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, "this is not an encrypted blob");
        var warnings = new List<string>();
        Assert.Null(new DpapiTokenStore(File_, warnings.Add).Load());
        Assert.Single(warnings);
    }

    [Fact]
    public void Unwritable_location_does_not_crash_the_service()
    {
        var warnings = new List<string>();
        new DpapiTokenStore(Path.Combine(Path.GetTempPath(), "x :invalid", "t.bin"), warnings.Add).Save("t");   // недопустимый путь
        Assert.NotEmpty(warnings);
    }
}
