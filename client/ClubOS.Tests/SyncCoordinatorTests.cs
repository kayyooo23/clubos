using System.Text.Json;
using ClubOS.Service;
using ClubOS.Service.Sync;
using ClubOS.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ClubOS.Tests;

/// <summary>Координатор: что ПК делает по заданию сервера. «Сервер» поддельный (принимает отчёты), «ПК» настоящие: у каждого своя папка и своя раздача по localhost.</summary>
public class SyncCoordinatorTests : SyncTestBase
{
    private static readonly SyncCoordinator.Timing Fast = new(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(100), 3, Block);

    private sealed class Pc(SyncNode node, SyncCoordinator coordinator, FakeServer server)
    {
        public SyncNode Node { get; } = node;
        public SyncCoordinator Coordinator { get; } = coordinator;
        public FakeServer Server { get; } = server;
        public bool Idle { get; set; } = true;

        public List<JsonDocument> Reports() => Server.To($"/api/agent/sync/{Job}/progress").Select(r => JsonDocument.Parse(r.Body)).ToList();
        public List<string> Statuses() => Reports().Select(d => d.RootElement.GetProperty("status").GetString()!).ToList();
        public JsonDocument? Last(string status) => Reports().LastOrDefault(d => d.RootElement.GetProperty("status").GetString() == status);
    }

    private Pc NewPc(string name, Func<FakeServer.Seen, (int, string)>? progress = null, SyncNode? existing = null)
    {
        var node = existing ?? Node(name);
        var server = new FakeServer();
        server.On("POST", $"/api/agent/sync/{Job}/manifest", 200, Samples.Ok);
        server.On("POST", $"/api/agent/sync/{Job}/progress", progress ?? (_ => (200, Samples.Ok)));
        var api = new ServerApi(new HttpClient(server) { BaseAddress = new Uri("http://club.test") }, FakeServer.PcKey);
        Pc? holder = null;
        var coordinator = new SyncCoordinator(api, Http, node.Server, node.Root, Path.Combine(Base, name + "-sync"), () => holder!.Idle, () => node.Url, Fast);
        holder = new Pc(node, coordinator, server);
        return holder;
    }

    private static string? Msg(JsonDocument d) => d.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;

    private static async Task WaitFor(Func<bool> condition, string what, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"не дождались: {what}");
            await Task.Delay(25);
        }
    }

    private static SyncAssignment ForSource(params string[] paths) => new(Job, "source", "pending", Token, paths.Length == 0 ? ["Games/Dota"] : paths, false);

    private static SyncAssignment ForTarget(string? digest, IReadOnlyList<SyncPeer>? peers, bool deleteExtra = false) =>
        new(Job, "target", "pending", Token, ["Games/Dota"], deleteExtra, digest, peers);

    // ---------- эталон ----------
    [Fact]
    public async Task The_source_builds_the_list_tells_the_server_its_digest_and_starts_sharing()
    {
        var source = Source();
        var pc = NewPc("source", existing: source);
        pc.Coordinator.Update([ForSource()]);
        await WaitFor(() => pc.Last("ready") is not null, "эталон готов");

        var (_, _, digest) = await Build(source);
        var posted = JsonDocument.Parse(pc.Server.To($"/api/agent/sync/{Job}/manifest").Single().Body).RootElement;
        Assert.Equal(digest, posted.GetProperty("sha256").GetString());
        Assert.Equal(4, posted.GetProperty("files").GetInt32());
        Assert.Equal(9024, posted.GetProperty("bytes").GetInt64());
        var ready = pc.Last("ready")!.RootElement;
        Assert.Equal(source.Url, ready.GetProperty("serve_url").GetString());
        Assert.Contains("scanning", pc.Statuses());
        Assert.True(source.Server.IsServing(Job));
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{source.Url}/sync/{Job}/manifest");
        req.Headers.Add("X-Sync-Token", Token);
        using var resp = await Http.SendAsync(req);
        Assert.Equal(digest, SyncManifest.Digest(await resp.Content.ReadAsByteArrayAsync()));
        await pc.Coordinator.StopAllAsync();
    }

    [Fact]
    public async Task A_source_with_a_missing_folder_or_changed_files_reports_the_reason()
    {
        var source = Source();
        var pc = NewPc("source", existing: source);
        pc.Coordinator.Update([ForSource("Games/Missing")]);
        await WaitFor(() => pc.Last("failed") is not null, "ошибка эталона");
        Assert.Contains("Не найдено", pc.Last("failed")!.RootElement.GetProperty("message").GetString());
        await pc.Coordinator.StopAllAsync();

        var other = Source2();
        var again = NewPc("source2", existing: other);
        again.Coordinator.Update([ForSource() with { ManifestSha256 = new string('0', 64) }]);                    // сервер ждёт другой список: файлы с тех пор изменились
        await WaitFor(() => again.Last("failed") is not null, "несовпадение списка");
        Assert.Contains("изменились", again.Last("failed")!.RootElement.GetProperty("message").GetString());
        Assert.Empty(again.Server.To($"/api/agent/sync/{Job}/manifest"));                                         // чужой список не регистрируем
        await again.Coordinator.StopAllAsync();
    }

    private SyncNode Source2()
    {
        var s = Node("source2");
        s.Write("Games/Dota/a.bin", 3000, 1);
        return s;
    }

    // ---------- получатель ----------
    private async Task<(SyncNode Source, string Digest)> SharedSource()
    {
        var source = Source();
        var (m, bytes, digest) = await Build(source);
        Share(source, m, bytes);
        return (source, digest);
    }

    [Fact]
    public async Task A_receiver_copies_the_game_and_then_keeps_sharing_it()
    {
        var (source, digest) = await SharedSource();
        var pc = NewPc("target");
        pc.Coordinator.Update([ForTarget(digest, [Peer(source, "source")])]);
        await WaitFor(() => pc.Last("ready") is not null, "получатель готов");
        AssertSameFiles(source, pc.Node);
        var ready = pc.Last("ready")!.RootElement;
        Assert.Equal(9024, ready.GetProperty("bytes_fetched").GetInt64());
        Assert.Equal(4, ready.GetProperty("files_done").GetInt32());
        Assert.Equal(pc.Node.Url, ready.GetProperty("serve_url").GetString());
        Assert.Contains("syncing", pc.Statuses());
        Assert.True(pc.Node.Server.IsServing(Job));                                                              // получив файлы, отдаёт их другим

        pc.Coordinator.Update([]);                                                                               // задание закрыто на сервере
        Assert.False(pc.Coordinator.IsRunning(Job));
        Assert.False(pc.Node.Server.IsServing(Job));
        await pc.Coordinator.StopAllAsync();
    }

    [Fact]
    public async Task A_receiver_waits_for_the_list_and_for_a_free_pc_before_touching_the_disk()
    {
        var (source, digest) = await SharedSource();
        var pc = NewPc("target");
        pc.Idle = false;
        pc.Coordinator.Update([ForTarget(null, null)]);
        await WaitFor(() => pc.Last("waiting") is not null, "ожидание списка");
        Assert.Contains("список файлов", pc.Last("waiting")!.RootElement.GetProperty("message").GetString());

        pc.Coordinator.Update([ForTarget(digest, [Peer(source, "source")])]);                                    // список появился, но за ПК гость
        await WaitFor(() => pc.Reports().Any(d => Msg(d)?.Contains("гость") == true), "ожидание освобождения");
        await Task.Delay(200);
        Assert.False(Directory.Exists(pc.Node.PathOf("Games")));                                                 // ничего не копируется, пока ПК занят
        Assert.DoesNotContain("syncing", pc.Statuses());

        pc.Idle = true;
        await WaitFor(() => pc.Last("ready") is not null, "готово после освобождения");
        AssertSameFiles(source, pc.Node);
        await pc.Coordinator.StopAllAsync();
    }

    [Fact]
    public async Task Copying_pauses_when_a_guest_sits_down_and_continues_when_the_pc_is_free_again()
    {
        var source = Source();
        source.Write("Games/Dota/big.bin", 1_500_000, 42);                                                       // много блоков: копирование идёт заметное время
        var (m, bytes, digest) = await Build(source);
        Share(source, m, bytes);
        var pc = NewPc("target");
        pc.Coordinator.Update([ForTarget(digest, [Peer(source, "source")])]);
        await WaitFor(() => pc.Last("syncing") is not null, "копирование началось");
        pc.Idle = false;                                                                                         // гость сел за ПК
        await WaitFor(() => pc.Reports().Any(d => Msg(d)?.StartsWith("Пауза") == true), "пауза");
        Assert.False(File.Exists(pc.Node.PathOf("Games/Dota/big.bin")));                                         // недокачанного файла в игре нет
        pc.Idle = true;
        await WaitFor(() => pc.Last("ready") is not null, "готово после паузы");
        AssertSameFiles(source, pc.Node);
        await pc.Coordinator.StopAllAsync();
    }

    [Fact]
    public async Task A_receiver_retries_when_no_source_is_reachable_and_succeeds_when_one_appears()
    {
        var (source, digest) = await SharedSource();
        var pc = NewPc("target");
        pc.Coordinator.Update([ForTarget(digest, [])]);
        await WaitFor(() => pc.Reports().Any(d => Msg(d)?.Contains("Нет участников") == true), "ожидание источника");
        Assert.DoesNotContain("failed", pc.Statuses());
        pc.Coordinator.Update([ForTarget(digest, [Peer(source, "source")])]);
        await WaitFor(() => pc.Last("ready") is not null, "готово, когда источник появился");
        AssertSameFiles(source, pc.Node);
        await pc.Coordinator.StopAllAsync();
    }

    [Fact]
    public async Task A_receiver_gives_up_with_a_clear_reason_when_every_source_sends_bad_data()
    {
        var source = Source();
        var liar = Node("liar");
        foreach (var f in new[] { ("Games/Dota/a.bin", 5000), ("Games/Dota/sub/b.bin", 1024), ("Games/Dota/c.dat", 3000), ("Games/Dota/empty.txt", 0) }) liar.Write(f.Item1, f.Item2, seed: 777);
        var (m, bytes, digest) = await Build(source);
        Share(source, m, bytes);
        Share(liar, m, bytes);
        var pc = NewPc("target");
        pc.Coordinator.Update([ForTarget(digest, [Peer(liar, "target")])]);
        await WaitFor(() => pc.Last("failed") is not null, "отказ после повторов");
        Assert.Contains("не прошёл проверку", pc.Last("failed")!.RootElement.GetProperty("message").GetString());
        Assert.Contains("waiting", pc.Statuses());                                                                // до отказа были попытки
        Assert.False(File.Exists(pc.Node.PathOf("Games/Dota/a.bin")));                                           // испорченного не осталось
        await pc.Coordinator.StopAllAsync();
    }

    [Fact]
    public async Task When_the_server_says_the_job_is_closed_the_receiver_stops_and_copies_nothing()
    {
        var (source, digest) = await SharedSource();
        var pc = NewPc("target", progress: _ => (409, Samples.Error("Задание уже закрыто")));
        pc.Coordinator.Update([ForTarget(digest, [Peer(source, "source")])]);
        await Task.Delay(600);
        Assert.False(Directory.Exists(pc.Node.PathOf("Games")));
        Assert.True(pc.Server.To($"/api/agent/sync/{Job}/progress").Count() <= 2);                               // дальше не донимает сервер
        await pc.Coordinator.StopAllAsync();
    }

    [Fact]
    public async Task Receivers_help_each_other_through_the_coordinator_too()
    {
        var (source, digest) = await SharedSource();
        var first = NewPc("first");
        first.Coordinator.Update([ForTarget(digest, [Peer(source, "source")])]);
        await WaitFor(() => first.Last("ready") is not null, "первый получатель готов");
        var second = NewPc("second");
        source.Server.Stop(Job);                                                                                  // эталон выключился: остался только первый получатель
        second.Coordinator.Update([ForTarget(digest, [Peer(first.Node, "target")])]);
        await WaitFor(() => second.Last("ready") is not null, "второй получатель готов");
        AssertSameFiles(source, second.Node);
        Assert.Equal(9024, second.Last("ready")!.RootElement.GetProperty("bytes_from_peers").GetInt64());
        await first.Coordinator.StopAllAsync();
        await second.Coordinator.StopAllAsync();
    }
}

public class GamesReporterTests : SyncTestBase
{
    [Fact]
    public async Task The_inventory_is_sent_on_request_and_then_not_again_until_it_is_due()
    {
        var node = Node("pc");
        node.Write("Tools/a.bin", 100, 1);
        node.Write("steamapps/common/Dota 2/g.bin", 500, 2);
        var server = new FakeServer();
        var api = new ServerApi(new HttpClient(server) { BaseAddress = new Uri("http://club.test") }, FakeServer.PcKey);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));
        var store = new MemoryTokenStore();
        var temp = Path.Combine(Base, "svc");
        var core = new AgentCore(api, new AgentConfig("http://club.test", FakeServer.PcKey), time, store, new AdImageCache(api, Path.Combine(temp, "ads")), new CoverCache(api, Path.Combine(temp, "covers")));
        server.On("GET", "/api/agent/state", 200, """{"mode":"locked","account":null,"session":null,"scan_games":true}""").On("POST", "/api/agent/games", 200, """{"ok":true,"count":2}""");
        await core.RefreshAsync();
        Assert.True(core.GamesRequested);

        var reporter = new GamesReporter(core, api, node.Root, time, NullLogger<GamesReporter>.Instance);
        Assert.True(await reporter.TickAsync(default));
        var sent = JsonDocument.Parse(server.To("/api/agent/games").Single().Body).RootElement;
        Assert.Equal(node.Root, sent.GetProperty("root").GetString());
        Assert.Equal(["Tools", "steamapps/common/Dota 2"], sent.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("path").GetString()));
        Assert.Equal(500, sent.GetProperty("entries")[1].GetProperty("size_bytes").GetInt64());
        Assert.False(core.GamesRequested);

        Assert.False(await reporter.TickAsync(default));                                                         // просьбы нет, срок не пришёл
        time.Advance(GamesReporter.Every + TimeSpan.FromMinutes(1));
        Assert.True(await reporter.TickAsync(default));                                                          // пора по расписанию
        Assert.Equal(2, server.To("/api/agent/games").Count());
    }
}
