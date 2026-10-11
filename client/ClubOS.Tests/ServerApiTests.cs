using System.Text.Json;
using ClubOS.Shared;

namespace ClubOS.Tests;

public class ServerApiTests
{
    private static (ServerApi Api, FakeServer Server) Create()
    {
        var server = new FakeServer();
        return (new ServerApi(new HttpClient(server) { BaseAddress = new Uri("http://club.test") }, FakeServer.PcKey), server);
    }

    [Fact]
    public async Task Every_request_carries_the_pc_key_and_token_only_when_given()
    {
        var (api, server) = Create();
        server.On("GET", "/api/agent/state", 200, Samples.Locked).On("GET", "/api/me", 200, "{}");
        await api.GetAsync<ServerState>("/api/agent/state");
        await api.GetAsync<AccountInfo>("/api/me", "tok123");
        Assert.All(server.Requests, r => Assert.Equal(FakeServer.PcKey, r.PcKey));
        Assert.Null(server.Requests[0].Auth);
        Assert.Equal("Bearer tok123", server.Requests[1].Auth);
    }

    [Fact]
    public async Task Post_sends_snake_case_json()
    {
        var (api, server) = Create();
        server.On("POST", "/api/auth/register/confirm", 200, Samples.RegisterOk);
        await api.PostAsync<JsonElement>("/api/auth/register/confirm", new { phone = "+7900", accept_terms = true });
        Assert.Contains("\"accept_terms\":true", server.Requests[0].Body);
    }

    [Theory]
    [InlineData(Samples.Locked, PcMode.Locked)]
    [InlineData(Samples.Cabinet, PcMode.Locked)]
    [InlineData(Samples.Playing, PcMode.GuestSession)]
    [InlineData(Samples.Staff, PcMode.StaffDesktop)]
    [InlineData(Samples.Suspended, PcMode.Locked)]
    public async Task Parses_every_state_shape_the_python_server_returns(string json, PcMode mode)
    {
        var (api, server) = Create();
        server.On("GET", "/api/agent/state", 200, json);
        var r = await api.GetAsync<ServerState>("/api/agent/state");
        Assert.True(r.Ok);
        Assert.Equal(mode, r.Value!.Mode);
    }

    [Fact]
    public async Task Parses_account_session_and_tariffs()
    {
        var (api, server) = Create();
        server.On("GET", "/api/agent/state", 200, Samples.Playing).On("GET", "/api/agent/tariffs", 200, Samples.Tariffs)
              .On("GET", "/api/me/transactions", 200, Samples.Transactions);
        var state = (await api.GetAsync<ServerState>("/api/agent/state")).Value!;
        Assert.Equal(250, state.Account!.Balance);
        Assert.Equal("+7 *** ***-00-01", state.Account.PhoneMasked);
        Assert.Equal(10800, state.Session!.SecondsLeft);
        Assert.Equal("3 часа", state.Session.Tariff);
        var tariffs = (await api.GetAsync<List<TariffInfo>>("/api/agent/tariffs")).Value!;
        Assert.Equal(["hourly", "package", "night"], tariffs.Select(t => t.Kind));
        Assert.Equal((23, 8), (tariffs[2].NightStart!.Value, tariffs[2].NightEnd!.Value));
        var tx = (await api.GetAsync<List<TransactionInfo>>("/api/me/transactions")).Value!;
        Assert.Equal(-250, tx[0].Amount);
        Assert.Equal("session_charge", tx[0].Kind);
    }

    [Fact]
    public async Task Server_error_text_is_passed_to_the_user()
    {
        var (api, server) = Create();
        server.On("POST", "/api/me/sessions", 402, Samples.Error("Не хватает 50 ₽ на балансе"));
        var r = await api.PostAsync<JsonElement>("/api/me/sessions", new { tariff_id = 2 }, "t");
        Assert.False(r.Ok);
        Assert.Equal(402, r.Status);
        Assert.Equal("Не хватает 50 ₽ на балансе", r.Error);
    }

    [Fact]
    public async Task Validation_errors_from_pydantic_become_readable()
    {
        var (api, server) = Create();
        server.On("POST", "/x", 422, """{"detail":[{"type":"value_error","loc":["body","phone"],"msg":"Value error, Некорректный номер телефона"}]}""");
        var r = await api.PostAsync<JsonElement>("/x");
        Assert.Equal("Некорректный номер телефона", r.Error);
    }

    [Fact]
    public async Task Garbage_and_empty_error_bodies_do_not_crash()
    {
        var (api, server) = Create();
        server.On("GET", "/a", 502, "<html>Bad gateway</html>").On("GET", "/b", 500, "").On("GET", "/c", 422, """{"detail":[]}""");
        Assert.Equal("Ошибка сервера (502)", (await api.GetAsync<JsonElement>("/a")).Error);
        Assert.Equal("Ошибка сервера (500)", (await api.GetAsync<JsonElement>("/b")).Error);
        Assert.Equal("Проверьте введённые данные", (await api.GetAsync<JsonElement>("/c")).Error);
    }

    [Fact]
    public async Task Unreachable_server_is_status_zero_not_an_exception()
    {
        var (api, server) = Create();
        server.Down = true;
        var r = await api.GetAsync<ServerState>("/api/agent/state");
        Assert.True(r.NetworkError);
        Assert.False(r.Ok);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_swallowed()
    {
        var (api, server) = Create();
        server.On("GET", "/api/agent/state", 200, Samples.Locked);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.GetAsync<ServerState>("/api/agent/state", null, cts.Token));
    }

    [Fact]
    public async Task Success_with_unreadable_body_is_an_error_not_a_crash()
    {
        var (api, server) = Create();
        server.On("GET", "/api/agent/state", 200, "not json");
        var r = await api.GetAsync<ServerState>("/api/agent/state");
        Assert.False(r.Ok);
        Assert.False(r.NetworkError);
    }
}
