using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ClubOS.Service.Sync;
using ClubOS.Shared;

namespace ClubOS.Tests;

/// <summary>Один «ПК» для тестов синхронизации: своя игровая папка и свой настоящий сервер раздачи на localhost.</summary>
public sealed class SyncNode : IDisposable
{
    private static int Free()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public string Root { get; }
    public SyncServer Server { get; }
    public string Url { get; }

    public SyncNode(string baseDir, string name)
    {
        Root = Path.Combine(baseDir, name);
        Directory.CreateDirectory(Root);
        var port = Free();
        Server = new SyncServer(Root, "127.0.0.1", port);
        Assert.True(Server.TryStart(out var error), error);
        Url = $"http://127.0.0.1:{port}";
    }

    public string PathOf(string rel) => Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Файл с заранее известным содержимым и временем изменения.</summary>
    public void Write(string rel, int size, int seed, long mtime = 1_800_000_000)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        Directory.CreateDirectory(Path.GetDirectoryName(PathOf(rel))!);
        File.WriteAllBytes(PathOf(rel), data);
        File.SetLastWriteTimeUtc(PathOf(rel), DateTimeOffset.FromUnixTimeSeconds(mtime).UtcDateTime);
    }

    public void Dispose() => Server.Dispose();
}

public abstract class SyncTestBase : IDisposable
{
    protected const int Block = 1024;
    protected const int Job = 7;
    protected const string Token = "job-secret-token-123";

    protected readonly string Base = Path.Combine(Path.GetTempPath(), "clubos-test-sync-" + Guid.NewGuid().ToString("N"));
    private readonly List<SyncNode> _nodes = [];
    protected readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    protected SyncTestBase() => Directory.CreateDirectory(Base);

    protected SyncNode Node(string name)
    {
        var node = new SyncNode(Base, name);
        _nodes.Add(node);
        return node;
    }

    /// <summary>Эталон с небольшой «игрой»: несколько файлов разного размера, вложенная папка, пустой файл.</summary>
    protected SyncNode Source()
    {
        var s = Node("source");
        s.Write("Games/Dota/a.bin", 5000, 1);          // пять блоков, последний короче
        s.Write("Games/Dota/sub/b.bin", 1024, 2);      // ровно один блок
        s.Write("Games/Dota/c.dat", 3000, 3);
        s.Write("Games/Dota/empty.txt", 0, 4);
        s.Write("Games/Other/readme.txt", 100, 5);
        return s;
    }

    protected async Task<(SyncManifest Manifest, byte[] Bytes, string Digest)> Build(SyncNode node, string[]? paths = null, IReadOnlyDictionary<string, ManifestFile>? previous = null)
    {
        var result = await new ManifestBuilder(node.Root, Block).BuildAsync(paths ?? ["Games/Dota"], previous, null, default);
        Assert.True(result.Manifest is not null, result.Error);
        var bytes = result.Manifest!.ToBytes();
        return (result.Manifest, bytes, SyncManifest.Digest(bytes));
    }

    protected void Share(SyncNode node, SyncManifest manifest, byte[] bytes) => node.Server.Serve(Job, Token, manifest, bytes);

    protected static SyncPeer Peer(SyncNode node, string role) => new(1, node.Url, role);

    protected SyncDownloader Downloader(SyncNode target, Func<string, long>? free = null) => new(Http, target.Root, 2, free ?? (_ => long.MaxValue / 4), new Random(1));

    protected Task<SyncOutcome> Sync(SyncNode target, SyncManifest manifest, IReadOnlyList<SyncPeer> peers, bool deleteExtra = false, string[]? roots = null,
        Func<CancellationToken, Task>? gate = null, Func<string, long>? free = null) =>
        Downloader(target, free).RunAsync(Job, Token, manifest, roots ?? ["Games/Dota"], deleteExtra, () => peers, _ => { }, gate, default);

    protected static void AssertSameFiles(SyncNode a, SyncNode b, string folder = "Games/Dota")
    {
        string[] Files(SyncNode n) => Directory.Exists(n.PathOf(folder))
            ? Directory.GetFiles(n.PathOf(folder), "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(n.Root, f)).Order().ToArray() : [];
        Assert.Equal(Files(a), Files(b));
        foreach (var rel in Files(a))
        {
            Assert.Equal(File.ReadAllBytes(Path.Combine(a.Root, rel)), File.ReadAllBytes(Path.Combine(b.Root, rel)));
            Assert.Equal(File.GetLastWriteTimeUtc(Path.Combine(a.Root, rel)), File.GetLastWriteTimeUtc(Path.Combine(b.Root, rel)));
        }
    }

    public void Dispose()
    {
        foreach (var n in _nodes) n.Dispose();
        Http.Dispose();
        try { Directory.Delete(Base, true); } catch (IOException) { }
    }
}

public class SyncPathTests : SyncTestBase
{
    [Theory]
    [InlineData("Games/Dota 2", "Games/Dota 2")]
    [InlineData("Games\\Dota\\a.bin", "Games/Dota/a.bin")]
    [InlineData("  a/b/ ", "a/b")]
    public void Good_paths_are_normalized(string raw, string expected) => Assert.Equal(expected, SyncPath.Normalize(raw));

    [Theory]
    [InlineData("")]
    [InlineData("/etc")]
    [InlineData("C:/Windows")]
    [InlineData("a/../b")]
    [InlineData("..")]
    [InlineData("a//b")]
    [InlineData("a/./b")]
    [InlineData("file.txt:stream")]
    [InlineData("a/b?")]
    [InlineData("a/CON")]
    [InlineData("a/nul.txt")]
    [InlineData("a/Com1.dat")]
    [InlineData("a /b")]
    [InlineData("a/b.")]
    public void Dangerous_paths_are_refused(string raw) => Assert.Null(SyncPath.Normalize(raw));

    [Fact]
    public void Resolve_stays_inside_the_root_and_refuses_links()
    {
        Directory.CreateDirectory(Path.Combine(Base, "root", "real"));
        Directory.CreateDirectory(Path.Combine(Base, "outside"));
        File.WriteAllText(Path.Combine(Base, "outside", "secret.txt"), "x");
        var root = Path.Combine(Base, "root");
        Assert.Equal(Path.Combine(root, "real", "f.bin"), SyncPath.Resolve(root, "real/f.bin"));
        Assert.Null(SyncPath.Resolve(root, "../outside/secret.txt"));
        var link = Path.Combine(root, "link");
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{Path.Combine(Base, "outside")}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
        using (var p = Process.Start(psi)!) { p.WaitForExit(); Assert.Equal(0, p.ExitCode); }
        try
        {
            Assert.Null(SyncPath.Resolve(root, "link/secret.txt"));                      // по ссылке за пределы игровой папки не выйти
            Assert.Null(SyncPath.Resolve(root, "link"));
            Assert.Equal("real/f.bin", SyncPath.Relative(root, Path.Combine(root, "real", "f.bin")));
        }
        finally { Directory.Delete(link, false); }                                       // саму ссылку убираем отдельно, иначе уборка папки теста спотыкается
    }
}

public class SyncManifestTests : SyncTestBase
{
    [Fact]
    public async Task The_manifest_lists_every_file_with_block_hashes_in_a_stable_order()
    {
        var source = Source();
        var (m, bytes, digest) = await Build(source);
        Assert.Equal(["Games/Dota/a.bin", "Games/Dota/c.dat", "Games/Dota/empty.txt", "Games/Dota/sub/b.bin"], m.Files.Select(f => f.Path));
        var a = m.Files[0];
        Assert.Equal(5000, a.Size);
        Assert.Equal(5, a.Blocks.Length);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source.PathOf("Games/Dota/a.bin"))[..1024])).ToLowerInvariant(), a.Blocks[0]);
        Assert.Empty(m.Files.Single(f => f.Path.EndsWith("empty.txt")).Blocks);
        Assert.Equal(1_800_000_000, a.Mtime);
        Assert.Equal(m.TotalBytes, 9024);
        var again = await Build(source);
        Assert.Equal(digest, again.Digest);                                                // тот же результат при каждом подсчёте: sha256 списка можно сверять
        var parsed = SyncManifest.Parse(bytes, out var error);
        Assert.True(parsed is not null, error);
        Assert.Equal(m.Files.Select(f => (f.Path, f.Size, f.Mtime)), parsed!.Files.Select(f => (f.Path, f.Size, f.Mtime)));
    }

    [Fact]
    public async Task Unchanged_files_reuse_their_previous_hashes_and_changed_ones_are_rehashed()
    {
        var source = Source();
        var (first, _, _) = await Build(source);
        var cache = first.Files.ToDictionary(f => f.Path);
        File.WriteAllBytes(source.PathOf("Games/Dota/c.dat"), new byte[3000]);             // изменили, но размер и время оставили прежними
        File.SetLastWriteTimeUtc(source.PathOf("Games/Dota/c.dat"), DateTimeOffset.FromUnixTimeSeconds(1_800_000_000).UtcDateTime);
        var (viaCache, _, _) = await Build(source, previous: cache);
        Assert.Equal(first.Files.Single(f => f.Path.EndsWith("c.dat")).Blocks, viaCache.Files.Single(f => f.Path.EndsWith("c.dat")).Blocks);   // взято из кэша, не пересчитано
        source.Write("Games/Dota/c.dat", 3000, 99, mtime: 1_800_000_500);                    // изменилось и время: пересчёт
        var (fresh, _, _) = await Build(source, previous: cache);
        Assert.NotEqual(first.Files.Single(f => f.Path.EndsWith("c.dat")).Blocks, fresh.Files.Single(f => f.Path.EndsWith("c.dat")).Blocks);
    }

    [Fact]
    public async Task Steam_games_bring_their_appmanifest_along()
    {
        var s = Node("steam");
        s.Write("steamapps/common/Dota 2/game.bin", 2000, 1);
        s.Write("steamapps/common/Other/x.bin", 100, 2);
        File.WriteAllText(s.PathOf("steamapps/appmanifest_570.acf"), "\"AppState\"\n{\n\t\"appid\"\t\t\"570\"\n\t\"installdir\"\t\t\"Dota 2\"\n}\n");
        File.WriteAllText(s.PathOf("steamapps/appmanifest_730.acf"), "\"AppState\"\n{\n\t\"installdir\"\t\t\"Counter-Strike\"\n}\n");
        var (m, _, _) = await Build(s, ["steamapps/common/Dota 2"]);
        Assert.Equal(["steamapps/appmanifest_570.acf", "steamapps/common/Dota 2/game.bin"], m.Files.Select(f => f.Path));
        Assert.Equal("Dota 2", SteamLibrary.ReadInstallDir(s.PathOf("steamapps/appmanifest_570.acf")));
        Assert.Empty(SteamLibrary.ExtraFiles(s.Root, ["Other/folder"]));
    }

    [Fact]
    public async Task Missing_paths_and_dangerous_paths_are_errors_not_empty_manifests()
    {
        var s = Source();
        var builder = new ManifestBuilder(s.Root, Block);
        Assert.Contains("Не найдено", (await builder.BuildAsync(["Games/Nope"], null, null, default)).Error);
        Assert.Contains("Недопустимый путь", (await builder.BuildAsync(["../x"], null, null, default)).Error);
        Directory.CreateDirectory(s.PathOf("Games/EmptyDir"));
        Assert.Contains("нет файлов", (await builder.BuildAsync(["Games/EmptyDir"], null, null, default)).Error);
    }

    [Fact]
    public async Task A_manifest_from_another_pc_is_rejected_if_it_is_unsafe_or_inconsistent()
    {
        var s = Source();
        var (m, _, _) = await Build(s);
        byte[] With(Func<ManifestFile, ManifestFile> change) => (m with { Files = [change(m.Files[0]), .. m.Files.Skip(1)] }).ToBytes();
        Assert.Null(SyncManifest.Parse(With(f => f with { Path = "../evil.txt" }), out var e1));
        Assert.Contains("путь", e1);
        Assert.Null(SyncManifest.Parse(With(f => f with { Path = "C:/Windows/evil.txt" }), out _));
        Assert.Null(SyncManifest.Parse(With(f => f with { Blocks = f.Blocks[..2] }), out var e2));                         // блоков меньше, чем нужно по размеру
        Assert.Contains("блоки", e2);
        Assert.Null(SyncManifest.Parse(With(f => f with { Blocks = [.. f.Blocks[..4], "xyz"] }), out _));
        Assert.Null(SyncManifest.Parse(With(f => f with { Size = -1 }), out _));
        Assert.Null(SyncManifest.Parse((m with { BlockSize = 10 }).ToBytes(), out _));
        Assert.Null(SyncManifest.Parse((m with { Version = 99 }).ToBytes(), out _));
        Assert.Null(SyncManifest.Parse((m with { Files = [m.Files[0], m.Files[0]] }).ToBytes(), out var dup));
        Assert.Contains("Повтор", dup);
        Assert.Null(SyncManifest.Parse("не json"u8.ToArray(), out _));
        Assert.NotNull(SyncManifest.Parse(m.ToBytes(), out _));
    }

    [Fact]
    public void The_games_inventory_lists_folders_and_steam_games_with_their_size()
    {
        var s = Node("inv");
        s.Write("Tools/a.bin", 300, 1);
        s.Write("Tools/sub/b.bin", 200, 2);
        s.Write("steamapps/common/Dota 2/g.bin", 5000, 3);
        s.Write("steamapps/appmanifest_570.acf", 10, 4);
        Directory.CreateDirectory(s.PathOf("$RECYCLE.BIN"));
        Directory.CreateDirectory(s.PathOf(".hidden"));
        var entries = GamesInventory.Scan(s.Root);
        Assert.Equal([("Tools", "dir", 500L, 2), ("steamapps/common/Dota 2", "steam", 5000L, 1)], entries.Select(e => (e.Path, e.Kind, e.SizeBytes, e.Files)));
        Assert.Equal("Dota 2", entries[1].Name);
        Assert.Empty(GamesInventory.Scan(Path.Combine(Base, "no-such-folder")));
    }
}

public class SyncTransferTests : SyncTestBase
{
    [Fact]
    public async Task The_server_requires_the_job_secret_and_serves_only_files_from_the_list()
    {
        var s = Source();
        var (m, bytes, digest) = await Build(s);
        Share(s, m, bytes);
        async Task<HttpResponseMessage> Get(string path, string? token = Token, string? range = null)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, s.Url + path);
            if (token is not null) req.Headers.Add("X-Sync-Token", token);
            if (range is not null) req.Headers.TryAddWithoutValidation("Range", range);
            return await Http.SendAsync(req);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get($"/sync/{Job}/manifest", token: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get($"/sync/{Job}/manifest", token: "wrong")).StatusCode);
        var ok = await Get($"/sync/{Job}/manifest");
        Assert.Equal(digest, SyncManifest.Digest(await ok.Content.ReadAsByteArrayAsync()));
        Assert.Equal(HttpStatusCode.NotFound, (await Get($"/sync/999/manifest")).StatusCode);                                 // чужое задание
        Assert.Equal(HttpStatusCode.NotFound, (await Get($"/sync/{Job}/file?path=Games/Other/readme.txt")).StatusCode);       // файл есть на диске, но не в списке
        Assert.Equal(HttpStatusCode.NotFound, (await Get($"/sync/{Job}/file?path=..%2Fsource%2FGames%2FDota%2Fa.bin")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Get($"/sync/{Job}/file")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Get("/other")).StatusCode);

        var full = await Get($"/sync/{Job}/file?path=Games/Dota/a.bin");
        Assert.Equal(HttpStatusCode.OK, full.StatusCode);
        Assert.Equal(File.ReadAllBytes(s.PathOf("Games/Dota/a.bin")), await full.Content.ReadAsByteArrayAsync());
        var part = await Get($"/sync/{Job}/file?path=Games/Dota/a.bin", range: "bytes=1024-2047");
        Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
        Assert.Equal(File.ReadAllBytes(s.PathOf("Games/Dota/a.bin"))[1024..2048], await part.Content.ReadAsByteArrayAsync());
        Assert.Equal("bytes 1024-2047/5000", part.Content.Headers.ContentRange!.ToString());
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, (await Get($"/sync/{Job}/file?path=Games/Dota/a.bin", range: "bytes=9000-")).StatusCode);

        File.AppendAllText(s.PathOf("Games/Dota/a.bin"), "x");                                                                // файл на диске уже не тот, что в списке
        Assert.Equal(HttpStatusCode.NotFound, (await Get($"/sync/{Job}/file?path=Games/Dota/a.bin")).StatusCode);
        s.Server.Stop(Job);
        Assert.Equal(HttpStatusCode.NotFound, (await Get($"/sync/{Job}/manifest")).StatusCode);
    }

    [Theory]
    [InlineData("bytes=0-9", true, 0L, 9L)]
    [InlineData("bytes=10-", true, 10L, 99L)]
    [InlineData("bytes=-5", true, 95L, 99L)]
    [InlineData("bytes=90-500", true, 90L, 99L)]
    [InlineData("bytes=100-", false, 0L, 0L)]
    [InlineData("bytes=5-2", false, 0L, 0L)]
    [InlineData("bytes=0-1,5-6", false, 0L, 0L)]
    [InlineData("items=0-1", false, 0L, 0L)]
    [InlineData("bytes=a-b", false, 0L, 0L)]
    public void Range_headers_are_parsed_strictly(string header, bool ok, long from, long to)
    {
        Assert.Equal(ok, SyncServer.TryParseRange(header, 100, out var f, out var t));
        if (ok) Assert.Equal((from, to), (f, t));
    }

    [Fact]
    public async Task A_receiver_gets_an_exact_copy_with_the_same_times_and_a_second_run_transfers_nothing()
    {
        var s = Source();
        var t = Node("target");
        var (m, bytes, _) = await Build(s);
        Share(s, m, bytes);
        var first = await Sync(t, m, [Peer(s, "source")]);
        Assert.True(first.Ok, first.Error);
        Assert.Equal(9024, first.Stats.BytesFetched);
        Assert.Equal(first.Stats.BytesTotal, first.Stats.BytesDone);
        Assert.Equal(4, first.Stats.FilesDone);
        AssertSameFiles(s, t);
        Assert.False(File.Exists(t.PathOf("Games/Other/readme.txt")));                                       // чего не выбрали, то не копируется
        Assert.Empty(Directory.GetFiles(t.Root, "*" + SyncDownloader.PartSuffix, SearchOption.AllDirectories));
        var second = await Sync(t, m, [Peer(s, "source")]);
        Assert.True(second.Ok);
        Assert.Equal(0, second.Stats.BytesFetched);                                                           // всё уже на месте
        Assert.Equal(9024, second.Stats.BytesDone);
    }

    [Fact]
    public async Task A_file_that_is_already_correct_and_differs_only_in_time_is_not_rewritten()
    {
        var s = Source();
        var t = Node("target");                                                                               // у получателя игру уже обновил сам Steam: то же содержимое, другое время
        t.Write("Games/Dota/a.bin", 5000, 1, mtime: 1_700_000_000);
        t.Write("Games/Dota/sub/b.bin", 1024, 2, mtime: 1_700_000_000);
        t.Write("Games/Dota/c.dat", 3000, 3, mtime: 1_700_000_000);
        t.Write("Games/Dota/empty.txt", 0, 4, mtime: 1_700_000_000);
        var (m, bytes, _) = await Build(s);
        Share(s, m, bytes);
        using var hold = new FileStream(t.PathOf("Games/Dota/a.bin"), FileMode.Open, FileAccess.Read, FileShare.Read);   // файл открыт: заменить его переименованием нельзя, а выставить время можно
        var r = await Sync(t, m, [Peer(s, "source")]);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(0, r.Stats.BytesFetched);                                                                 // по сети не пришло ничего
        Assert.Equal(9024, r.Stats.BytesDone);
        Assert.Equal(4, r.Stats.FilesDone);
        hold.Dispose();
        AssertSameFiles(s, t);                                                                                 // и время теперь как в списке
        Assert.Empty(Directory.GetFiles(t.Root, "*" + SyncDownloader.PartSuffix, SearchOption.AllDirectories));
    }

    [Fact]
    public async Task After_a_game_update_only_the_changed_blocks_travel()
    {
        var s = Source();
        var t = Node("target");
        var (m1, b1, _) = await Build(s);
        Share(s, m1, b1);
        Assert.True((await Sync(t, m1, [Peer(s, "source")])).Ok);

        var path = s.PathOf("Games/Dota/a.bin");                                                              // «патч»: один блок в середине большого файла
        var data = File.ReadAllBytes(path);
        Array.Fill(data, (byte)0xAB, 2048, 1024);
        File.WriteAllBytes(path, data);
        File.SetLastWriteTimeUtc(path, DateTimeOffset.FromUnixTimeSeconds(1_800_001_000).UtcDateTime);
        var (m2, b2, _) = await Build(s, previous: m1.Files.ToDictionary(f => f.Path));
        Share(s, m2, b2);
        var update = await Sync(t, m2, [Peer(s, "source")]);
        Assert.True(update.Ok, update.Error);
        Assert.Equal(1024, update.Stats.BytesFetched);                                                         // вместо 5000 байт файла пришёл один блок
        AssertSameFiles(s, t);
    }

    [Fact]
    public async Task An_old_copy_of_a_file_is_reused_block_by_block()
    {
        var s = Source();
        var t = Node("target");
        var (m, bytes, _) = await Build(s);
        Share(s, m, bytes);
        var original = File.ReadAllBytes(s.PathOf("Games/Dota/c.dat"));
        var older = (byte[])original.Clone();
        Array.Fill(older, (byte)0, 0, 1024);                                                                   // у получателя старая версия: первый блок отличается
        Directory.CreateDirectory(t.PathOf("Games/Dota"));
        File.WriteAllBytes(t.PathOf("Games/Dota/c.dat"), older);
        var r = await Sync(t, m, [Peer(s, "source")]);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(1024 + 5000 + 1024, r.Stats.BytesFetched);                                                // c.dat: один блок; a.bin и b.bin целиком; пустой файл ничего не стоит
        Assert.Equal(original, File.ReadAllBytes(t.PathOf("Games/Dota/c.dat")));
    }

    [Fact]
    public async Task Receivers_prefer_each_other_over_the_source_and_fall_back_to_it_for_what_they_lack()
    {
        var s = Source();
        var a = Node("a");
        var b = Node("b");
        var (m, bytes, _) = await Build(s);
        Share(s, m, bytes);
        Assert.True((await Sync(a, m, [Peer(s, "source")])).Ok);
        Share(a, m, bytes);                                                                                    // A получил всё и раздаёт
        File.Delete(a.PathOf("Games/Dota/c.dat"));                                                             // кроме одного файла
        var r = await Sync(b, m, [Peer(s, "source"), Peer(a, "target")]);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(9024, r.Stats.BytesFetched);
        Assert.Equal(9024 - 3000, r.Stats.BytesFromPeers);                                                     // всё, что было у A, взято у A; недостающее c.dat у эталона
        AssertSameFiles(s, b);
    }

    [Fact]
    public async Task A_peer_that_sends_wrong_data_is_skipped_and_a_copy_with_only_bad_sources_changes_nothing()
    {
        var s = Source();
        var liar = Node("liar");
        foreach (var f in new[] { ("Games/Dota/a.bin", 5000), ("Games/Dota/sub/b.bin", 1024), ("Games/Dota/c.dat", 3000), ("Games/Dota/empty.txt", 0) })
            liar.Write(f.Item1, f.Item2, seed: 777);                                                            // те же размеры и время, другое содержимое
        var (m, bytes, _) = await Build(s);
        Share(s, m, bytes);
        Share(liar, m, bytes);

        var good = Node("good");
        var ok = await Sync(good, m, [Peer(liar, "target"), Peer(s, "source")]);
        Assert.True(ok.Ok, ok.Error);
        AssertSameFiles(s, good);                                                                              // враньё лжеца отброшено, файлы настоящие

        var victim = Node("victim");
        victim.Write("Games/Dota/c.dat", 3000, seed: 5, mtime: 1_700_000_000);                                // у получателя уже что-то лежит
        var before = File.ReadAllBytes(victim.PathOf("Games/Dota/c.dat"));
        var bad = await Sync(victim, m, [Peer(liar, "target")]);
        Assert.False(bad.Ok);
        Assert.Contains("не прошёл проверку", bad.Error);
        Assert.Equal(before, File.ReadAllBytes(victim.PathOf("Games/Dota/c.dat")));                            // прежний файл цел
        Assert.Empty(Directory.GetFiles(victim.Root, "*" + SyncDownloader.PartSuffix, SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Without_any_reachable_peer_the_receiver_fails_cleanly()
    {
        var s = Source();
        var t = Node("target");
        var (m, _, _) = await Build(s);
        var none = await Sync(t, m, []);
        Assert.False(none.Ok);
        Assert.Contains("Нет участников", none.Error);
        var dead = await Sync(t, m, [new SyncPeer(1, "http://127.0.0.1:1", "source")]);
        Assert.False(dead.Ok);
        var outside = await Sync(t, m, [new SyncPeer(1, "http://8.8.8.8:47900", "source")]);                  // не локальный адрес: даже не пробуем
        Assert.False(outside.Ok);
        Assert.Contains("Нет участников", outside.Error);
    }

    [Theory]
    [InlineData("http://192.168.1.5:47900", true)]
    [InlineData("http://10.1.2.3:80", true)]
    [InlineData("http://172.16.0.1:1", true)]
    [InlineData("http://172.31.255.255:65535", true)]
    [InlineData("http://127.0.0.1:47900", true)]
    [InlineData("http://172.32.0.1:47900", false)]
    [InlineData("http://8.8.8.8:47900", false)]
    [InlineData("https://192.168.1.5:47900", false)]
    [InlineData("http://example.com:47900", false)]
    [InlineData("http://192.168.1.5", false)]
    [InlineData("http://192.168.1.5:0", false)]
    [InlineData("http://[::1]:47900", false)]
    [InlineData(null, false)]
    public void Only_private_network_addresses_are_ever_contacted(string? url, bool lan) => Assert.Equal(lan, SyncDownloader.IsLanUrl(url));

    [Fact]
    public async Task The_manifest_is_accepted_only_if_it_matches_the_digest_the_server_announced()
    {
        var s = Source();
        var (m, bytes, digest) = await Build(s);
        Share(s, m, bytes);
        var t = Node("t");
        var good = await Downloader(t).FetchManifestAsync(Job, Token, digest, [Peer(s, "source")], default);
        Assert.NotNull(good.Manifest);
        var wrong = await Downloader(t).FetchManifestAsync(Job, Token, new string('0', 64), [Peer(s, "source")], default);
        Assert.Null(wrong.Manifest);
        Assert.Contains("не совпал", wrong.Error);
        var denied = await Downloader(t).FetchManifestAsync(Job, "wrong-token", digest, [Peer(s, "source")], default);
        Assert.Null(denied.Manifest);
        Assert.Null((await Downloader(t).FetchManifestAsync(Job, Token, digest, [], default)).Manifest);
    }

    [Fact]
    public async Task Exact_mirror_removes_only_extras_inside_the_chosen_folders()
    {
        var s = Source();
        var t = Node("target");
        t.Write("Games/Dota/old-version.pak", 500, 8);
        t.Write("Games/Dota/oldsub/junk.txt", 50, 9);
        t.Write("Games/Dota/a.bin" + SyncDownloader.PartSuffix, 10, 10);
        t.Write("Games/Other/keep.txt", 70, 11);                                                              // вне выбранной папки: не трогаем
        var (m, bytes, _) = await Build(s);
        Share(s, m, bytes);
        var kept = await Sync(t, m, [Peer(s, "source")]);
        Assert.True(kept.Ok);
        Assert.True(File.Exists(t.PathOf("Games/Dota/old-version.pak")));                                      // без зеркала лишнее остаётся
        Assert.False(File.Exists(t.PathOf("Games/Dota/a.bin" + SyncDownloader.PartSuffix)));                   // а недокачанные хвосты прошлых попыток убираются всегда
        var mirrored = await Sync(t, m, [Peer(s, "source")], deleteExtra: true);
        Assert.True(mirrored.Ok);
        Assert.False(File.Exists(t.PathOf("Games/Dota/old-version.pak")));
        Assert.False(Directory.Exists(t.PathOf("Games/Dota/oldsub")));
        Assert.True(File.Exists(t.PathOf("Games/Other/keep.txt")));
        AssertSameFiles(s, t);
    }

    [Fact]
    public async Task A_dangerous_manifest_writes_nothing_and_not_enough_disk_space_stops_before_downloading()
    {
        var s = Source();
        var t = Node("target");
        var (m, bytes, _) = await Build(s);
        Share(s, m, bytes);
        var evil = m with { Files = [new ManifestFile("../evil.txt", 3, 1, [m.Files[0].Blocks[0]]), .. m.Files] };
        var r = await Sync(t, evil, [Peer(s, "source")]);
        Assert.False(r.Ok);
        Assert.Contains("Недопустимый путь", r.Error);
        Assert.False(File.Exists(Path.Combine(t.Root, "..", "evil.txt")));
        Assert.False(Directory.Exists(t.PathOf("Games")));

        var calls = 0;
        var small = await Downloader(t, free: _ => 100).RunAsync(Job, Token, m, ["Games/Dota"], false, () => { calls++; return [Peer(s, "source")]; }, _ => { }, null, default);
        Assert.False(small.Ok);
        Assert.Contains("Не хватает места", small.Error);
        Assert.Equal(0, calls);                                                                               // ни одного запроса: сначала проверка места
    }

    [Fact]
    public async Task The_pause_gate_is_consulted_before_every_block_and_can_stop_the_copy()
    {
        var s = Source();
        var t = Node("target");
        var (m, bytes, _) = await Build(s);
        Share(s, m, bytes);
        var gateCalls = 0;
        var ok = await Sync(t, m, [Peer(s, "source")], gate: _ => { Interlocked.Increment(ref gateCalls); return Task.CompletedTask; });
        Assert.True(ok.Ok);
        Assert.Equal(5 + 1 + 3, gateCalls);                                                                    // по одному разу на каждый блок: 5 + 1 + 3 (пустой файл блоков не имеет)

        var t2 = Node("target2");
        using var cts = new CancellationTokenSource();
        var stopped = 0;
        var task = Downloader(t2).RunAsync(Job, Token, m, ["Games/Dota"], false, () => [Peer(s, "source")], _ => { }, async ct => { if (Interlocked.Increment(ref stopped) > 2) { cts.Cancel(); ct.ThrowIfCancellationRequested(); } await Task.CompletedTask; }, cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);                                   // отмена задания прерывает копирование
        Assert.Empty(Directory.GetFiles(t2.Root, "*" + SyncDownloader.PartSuffix, SearchOption.AllDirectories));
    }
}
