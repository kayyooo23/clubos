using System.Net.Http;
using ClubOS.Service;
using ClubOS.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ClubOS.Tests;

/// <summary>Служба присылает список программ: при запуске, раз в пару часов только если он изменился, и сразу по просьбе администратора.</summary>
public sealed class ProgramsReporterTests
{
    private static readonly ProgramEntry Ts = new("TeamSpeak 3 Client", @"C:\Program Files\TeamSpeak 3 Client\ts3client_win64.exe");
    private static readonly ProgramEntry Chrome = new("Google Chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe");

    private sealed class Rig
    {
        public FakeServer Server { get; }
        public AgentCore Core { get; }
        public FakeTimeProvider Time { get; }
        public List<ProgramEntry> Found { get; } = [];
        public int Scans { get; private set; }
        public ProgramsReporter Reporter { get; }

        public Rig()
        {
            (Core, Server, Time) = Make.Core();                                    // служба и отправитель говорят с одним и тем же поддельным сервером
            var api = new ServerApi(new HttpClient(Server) { BaseAddress = new Uri("http://club.test") }, FakeServer.PcKey);
            Server.On("POST", "/api/agent/programs", 200, """{"ok":true,"accepted":1}""");
            Reporter = new ProgramsReporter(api, Core, () => { Scans++; return Found.ToList(); }, Time, NullLogger<ProgramsReporter>.Instance);
        }

        public async Task<bool> Tick() => await Reporter.TickAsync(CancellationToken.None);

        /// <summary>Сервер ответил на опрос state, что просил обновить список (или нет).</summary>
        public async Task ServerState(bool scan)
        {
            Server.On("GET", "/api/agent/state", 200, scan ? """{"mode":"locked","account":null,"session":null,"scan_programs":true}""" : """{"mode":"locked","account":null,"session":null}""");
            await Core.RefreshAsync();
        }
    }

    [Fact]
    public async Task The_first_check_sends_the_list_with_names_and_paths()
    {
        var rig = new Rig();
        rig.Found.AddRange([Ts, Chrome]);
        Assert.True(await rig.Tick());
        var sent = Assert.Single(rig.Server.To("/api/agent/programs"));
        Assert.Equal(FakeServer.PcKey, sent.PcKey);
        Assert.Contains("TeamSpeak 3 Client", sent.Body);
        Assert.Contains(@"ts3client_win64.exe", sent.Body);
        Assert.Contains("\"name\"", sent.Body);
        Assert.Contains("\"path\"", sent.Body);
    }

    [Fact]
    public async Task Until_the_next_time_comes_nothing_is_scanned_or_sent()
    {
        var rig = new Rig();
        rig.Found.Add(Ts);
        await rig.Tick();
        rig.Time.Advance(TimeSpan.FromMinutes(30));
        Assert.False(await rig.Tick());
        Assert.Equal(1, rig.Scans);
        Assert.Single(rig.Server.To("/api/agent/programs"));
    }

    [Fact]
    public async Task After_two_hours_it_looks_again_but_sends_only_when_the_list_changed()
    {
        var rig = new Rig();
        rig.Found.Add(Ts);
        await rig.Tick();
        rig.Time.Advance(ProgramsReporter.Every + TimeSpan.FromMinutes(1));
        Assert.False(await rig.Tick());                                           // тот же список: сервер уже знает
        Assert.Equal(2, rig.Scans);
        rig.Time.Advance(ProgramsReporter.Every + TimeSpan.FromMinutes(1));
        rig.Found.Add(Chrome);                                                    // поставили новую программу
        Assert.True(await rig.Tick());
        Assert.Equal(2, rig.Server.To("/api/agent/programs").Count());
    }

    [Fact]
    public async Task An_admin_request_makes_it_scan_and_send_at_once_even_if_nothing_changed()
    {
        var rig = new Rig();
        rig.Found.Add(Ts);
        await rig.Tick();
        await rig.ServerState(scan: true);
        Assert.True(rig.Core.ProgramsRequested);
        Assert.True(await rig.Tick());
        Assert.Equal(2, rig.Server.To("/api/agent/programs").Count());
        Assert.False(rig.Core.ProgramsRequested);                                 // просьба выполнена, второй раз не отправляем
        Assert.False(await rig.Tick());
    }

    [Fact]
    public async Task The_request_flag_follows_the_server_state()
    {
        var rig = new Rig();
        Assert.False(rig.Core.ProgramsRequested);
        await rig.ServerState(scan: true);
        Assert.True(rig.Core.ProgramsRequested);
        await rig.ServerState(scan: false);
        Assert.False(rig.Core.ProgramsRequested);
    }

    [Fact]
    public async Task A_failed_send_is_retried_in_a_few_minutes_and_the_request_stays()
    {
        var rig = new Rig();
        rig.Found.Add(Ts);
        rig.Server.On("POST", "/api/agent/programs", 500, """{"detail":"сбой"}""");
        await rig.ServerState(scan: true);
        Assert.False(await rig.Tick());
        Assert.True(rig.Core.ProgramsRequested);                                  // просьба не потеряна
        rig.Server.On("POST", "/api/agent/programs", 200, """{"ok":true,"accepted":1}""");
        Assert.True(await rig.Tick());                                            // пока просьба в силе, пробуем снова сразу
        Assert.False(rig.Core.ProgramsRequested);
    }

    [Fact]
    public async Task Without_a_request_a_failed_send_waits_before_the_next_try()
    {
        var rig = new Rig();
        rig.Found.Add(Ts);
        rig.Server.On("POST", "/api/agent/programs", 500, """{"detail":"сбой"}""");
        Assert.False(await rig.Tick());
        Assert.False(await rig.Tick());                                           // не долбим сервер
        Assert.Single(rig.Server.To("/api/agent/programs"));
        rig.Server.On("POST", "/api/agent/programs", 200, """{"ok":true,"accepted":1}""");
        rig.Time.Advance(ProgramsReporter.RetryAfter + TimeSpan.FromSeconds(1));
        Assert.True(await rig.Tick());
    }
}
