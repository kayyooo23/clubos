using System.IO.Pipes;
using System.Text;
using ClubOS.Service;
using ClubOS.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClubOS.Tests;

/// <summary>Настоящий именованный канал внутри теста: проверяем и протокол, и права доступа (PipeSecurity).</summary>
public sealed class PipeTests : IAsyncLifetime
{
    private readonly string _pipe = "ClubOS.Test." + Guid.NewGuid().ToString("N");
    private readonly FakeServer _server = new();
    private readonly CancellationTokenSource _stop = new();
    private PipeServer _pipeServer = null!;
    private AgentCore _core = null!;

    public async Task InitializeAsync()
    {
        _server.On("GET", "/api/agent/state", 200, Samples.Cabinet)
               .On("POST", "/api/auth/login", 200, Samples.LoginOk)
               .On("GET", "/api/agent/tariffs", 200, Samples.Tariffs);
        var http = new HttpClient(_server) { BaseAddress = new Uri("http://club.test") };
        var store = new MemoryTokenStore();
        store.Save("saved-token");                       // гость уже вошёл, поэтому кабинет виден
        _core = new AgentCore(new ServerApi(http, FakeServer.PcKey), new AgentConfig("http://club.test", FakeServer.PcKey), null, store);
        _pipeServer = new PipeServer(_core, _pipe, NullLogger<PipeServer>.Instance);
        await _pipeServer.StartAsync(_stop.Token);
        await _core.RefreshAsync();
    }

    public async Task DisposeAsync()
    {
        _stop.Cancel();
        await _pipeServer.StopAsync(CancellationToken.None);
    }

    private PipeAgentClient Client() => new(_pipe, TimeSpan.FromSeconds(20), connectMilliseconds: 10000);   // запас на нагруженную машину: сразу после сборки антивирус проверяет новые файлы

    [Fact]
    public async Task State_round_trip_over_a_real_pipe()
    {
        var r = await Client().SendAsync(new IpcRequest(7, Ops.State));
        Assert.True(r.Ok);
        Assert.Equal(500, r.Snapshot!.Account!.Balance);
        Assert.Equal(PcMode.Locked, r.Snapshot.Mode);
    }

    [Fact]
    public async Task Login_over_the_pipe_never_leaks_key_or_token_on_the_wire()
    {
        // читаем ответ «сырыми» байтами, как это сделал бы любой процесс в сессии гостя
        await using var raw = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await raw.ConnectAsync(2000);
        await PipeLine.WriteAsync(raw, new IpcRequest(1, Ops.Login, "+79000000001", "pw-123456"), CancellationToken.None);
        var line = await PipeLine.ReadLineAsync(raw, CancellationToken.None);
        Assert.NotNull(line);
        Assert.DoesNotContain(FakeServer.PcKey, line);
        Assert.DoesNotContain(FakeServer.GuestToken, line);
        Assert.DoesNotContain("pw-123456", line);
        Assert.Contains("\"ok\":true", line);
    }

    [Fact]
    public async Task Garbage_is_answered_with_an_error_and_the_server_keeps_working()
    {
        await using (var raw = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await raw.ConnectAsync(2000);
            await raw.WriteAsync(Encoding.UTF8.GetBytes("this is not json\n"));
            var line = await PipeLine.ReadLineAsync(raw, CancellationToken.None);
            Assert.Contains("\"ok\":false", line);
            Assert.Contains("400", line);
        }
        Assert.True((await Client().SendAsync(new IpcRequest(1, Ops.State))).Ok);
    }

    [Fact]
    public async Task Oversized_message_is_dropped_without_harming_the_service()
    {
        await using (var raw = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await raw.ConnectAsync(2000);
            var big = new byte[PipeLine.MaxBytes + 1000];
            Array.Fill(big, (byte)'a');
            try { await raw.WriteAsync(big); await raw.WriteAsync("\n"u8.ToArray()); } catch (IOException) { /* сервер мог закрыть раньше */ }
            string? line = null;
            try { line = await PipeLine.ReadLineAsync(raw, CancellationToken.None); } catch (IOException) { }
            if (line is not null) Assert.Contains("\"ok\":false", line);
        }
        Assert.True((await Client().SendAsync(new IpcRequest(1, Ops.State))).Ok);
    }

    [Fact]
    public async Task Client_that_connects_and_hangs_up_is_harmless()
    {
        for (var i = 0; i < 5; i++)
        {
            await using var raw = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            await raw.ConnectAsync(2000);
        }
        Assert.True((await Client().SendAsync(new IpcRequest(1, Ops.State))).Ok);
    }

    [Fact]
    public async Task Many_parallel_clients_all_get_answers()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Client().SendAsync(new IpcRequest(i, Ops.State))));
        Assert.All(results.Select((r, i) => (r, i)), x => { Assert.True(x.r.Ok); Assert.Equal(x.i, x.r.Id); });
    }

    [Fact]
    public async Task Tariffs_arrive_through_the_pipe()
    {
        var r = await Client().SendAsync(new IpcRequest(1, Ops.Tariffs));
        Assert.Equal(["Час", "3 часа", "Ночь"], r.Tariffs!.Select(t => t.Name));
    }

    [Fact]
    public async Task Missing_service_is_reported_not_thrown()
    {
        var r = await new PipeAgentClient("ClubOS.NoSuchPipe." + Guid.NewGuid().ToString("N"), TimeSpan.FromSeconds(3))
            .SendAsync(new IpcRequest(1, Ops.State));
        Assert.False(r.Ok);
        Assert.Equal(0, r.Status);
        Assert.Contains("недоступна", r.Error);
    }

    [Fact]
    public void Pipe_security_denies_the_network_and_allows_local_users()
    {
        var rules = PipeServer.BuildSecurity()
            .GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<PipeAccessRule>().ToList();
        Assert.Contains(rules, r => r.AccessControlType == System.Security.AccessControl.AccessControlType.Deny
            && r.IdentityReference.Value == "S-1-5-2");   // NETWORK
        Assert.Contains(rules, r => r.AccessControlType == System.Security.AccessControl.AccessControlType.Allow
            && r.IdentityReference.Value == "S-1-5-11");  // Authenticated Users
    }
}
