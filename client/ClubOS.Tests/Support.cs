using System.Net;
using System.Text;
using ClubOS.Service;
using ClubOS.Shared;
using Microsoft.Extensions.Time.Testing;

namespace ClubOS.Tests;

/// <summary>Поддельный сервер: тест заранее говорит, что ответить на «метод путь», а потом смотрит, какие запросы пришли.</summary>
public sealed class FakeServer : HttpMessageHandler
{
    public const string PcKey = "test-pc-key-123";
    public const string GuestToken = "guest-token-abc";

    public sealed record Seen(HttpMethod Method, string Path, string? Auth, string? PcKey, string Body, string Query = "");

    private readonly Dictionary<string, Func<Seen, (int Status, string Json)>> _routes = new();
    private readonly Dictionary<string, Func<Seen, (int Status, byte[] Body)>> _binary = new();
    public List<Seen> Requests { get; } = [];
    public bool Down { get; set; }   // «сервер недоступен»

    public FakeServer On(string method, string path, int status, string json) =>
        On(method, path, _ => (status, json));

    public FakeServer On(string method, string path, Func<Seen, (int, string)> reply)
    {
        _routes[$"{method} {path}"] = reply;
        return this;
    }

    /// <summary>Ответ-файл (картинка): тело отдаётся байтами, как у настоящего сервера.</summary>
    public FakeServer OnBytes(string method, string path, int status, byte[] body) => OnBytes(method, path, _ => (status, body));

    public FakeServer OnBytes(string method, string path, Func<Seen, (int, byte[])> reply)
    {
        _binary[$"{method} {path}"] = reply;
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Down) throw new HttpRequestException("сервер недоступен (тест)");
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var seen = new Seen(request.Method, request.RequestUri!.AbsolutePath,
            request.Headers.Authorization?.ToString(),
            request.Headers.TryGetValues("X-PC-Key", out var k) ? k.First() : null, body, request.RequestUri.Query);
        lock (Requests) Requests.Add(seen);
        if (_binary.TryGetValue($"{request.Method} {seen.Path}", out var file))
        {
            var (code, bytes) = file(seen);
            var answer = new HttpResponseMessage((HttpStatusCode)code) { Content = new ByteArrayContent(bytes) };
            answer.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            return answer;
        }
        if (!_routes.TryGetValue($"{request.Method} {seen.Path}", out var route))
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            { Content = new StringContent("{\"detail\":\"нет маршрута в тесте\"}", Encoding.UTF8, "application/json") };
        var (status, json) = route(seen);
        return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    public IEnumerable<Seen> To(string path) { lock (Requests) return Requests.Where(r => r.Path == path).ToList(); }
}

public static class Samples
{
    // Настоящие формы ответов Python-сервера (сняты с живого сервера при проверке).
    public const string Locked = """{"mode":"locked","account":null,"session":null}""";
    public const string Cabinet = """{"mode":"locked","account":{"id":2,"role":"guest","phone_masked":"+7 *** ***-00-01","balance":500},"session":null}""";
    public const string Playing = """{"mode":"guest_session","account":{"id":2,"role":"guest","phone_masked":"+7 *** ***-00-01","balance":250},"session":{"id":1,"pc_id":1,"account_id":2,"tariff":"3 часа","started_at":"2026-10-06T09:11:09.727397","planned_end":"2026-10-06T12:11:09.727397","ended_at":null,"cost":250,"seconds_left":10800}}""";
    public const string Staff = """{"mode":"staff_desktop","account":{"role":"staff"},"session":null}""";
    public const string Suspended = """{"mode":"locked","account":null,"session":null,"message":"Клуб приостановлен"}""";
    public const string Tariffs = """[{"id":1,"name":"Час","kind":"hourly","price":120,"package_minutes":null,"night_start":null,"night_end":null},{"id":2,"name":"3 часа","kind":"package","price":250,"package_minutes":180,"night_start":null,"night_end":null},{"id":3,"name":"Ночь","kind":"night","price":400,"package_minutes":null,"night_start":23,"night_end":8}]""";
    public const string LoginOk = """{"token":"guest-token-abc","account":{"id":2,"role":"guest","phone_masked":"+7 *** ***-00-01","balance":500},"mode":"locked"}""";
    public const string RegisterOk = """{"token":"guest-token-abc","account":{"id":2,"role":"guest","phone_masked":"+7 *** ***-00-01","balance":0}}""";
    public const string Apps = """[{"id":1,"title":"Dota 2","tab":"games","category":"MOBA","age_rating":12,"launch_kind":"steam","launch_target":"570"},{"id":2,"title":"Chrome","tab":"apps","category":"Браузеры","age_rating":null,"launch_kind":"exe","launch_target":"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe"}]""";
    public const string Resend = """{"resend_after":60}""";
    public const string Ok = """{"ok":true}""";
    public const string Transactions = """[{"id":2,"amount":-250,"balance_after":250,"kind":"session_charge","created_at":"2026-10-06T09:11:09.7"},{"id":1,"amount":500,"balance_after":500,"kind":"topup_cash","created_at":"2026-10-06T09:10:00"}]""";
    public static string Error(string detail) => $$"""{"detail":"{{detail}}"}""";
}

public static class Make
{
    private static readonly List<string> TempDirs = [];

    static Make()
    {
        // временные папки кэшей удаляются, когда тестовый процесс завершается
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            lock (TempDirs)
                foreach (var dir in TempDirs)
                    try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        };
    }

    private static string TempDir(string kind)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"clubos-test-{kind}-" + Guid.NewGuid().ToString("N"));
        lock (TempDirs) TempDirs.Add(dir);
        return dir;
    }

    public static (AgentCore Core, FakeServer Server, FakeTimeProvider Time) Core(int graceSeconds = 120, ITokenStore? store = null, string? adsDir = null, string? coversDir = null)
    {
        var server = new FakeServer();
        var http = new HttpClient(server) { BaseAddress = new Uri("http://club.test") };
        var cfg = new AgentConfig("http://club.test", FakeServer.PcKey, graceSeconds);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
        var api = new ServerApi(http, FakeServer.PcKey);
        // тесты не должны писать в настоящую папку баннеров: у каждого ядра своя временная
        var ads = new AdImageCache(api, adsDir ?? TempDir("ads"));
        var covers = new CoverCache(api, coversDir ?? TempDir("covers"));
        return (new AgentCore(api, cfg, time, store, ads, covers), server, time);
    }
}

/// <summary>Поддельная «служба» для тестов экранов: отвечает по таблице и запоминает запросы.</summary>
public sealed class FakeAgent : IAgentClient
{
    public List<IpcRequest> Sent { get; } = [];
    public Func<IpcRequest, IpcResponse> Handler { get; set; } = r => new IpcResponse(r.Id, true, 200, null, AgentSnapshot.Booting);
    public AgentSnapshot Current { get; set; } = AgentSnapshot.Booting;

    public Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken ct = default)
    {
        Sent.Add(request);
        return Task.FromResult(Handler(request));
    }

    public IpcResponse Ok(IpcRequest r, AgentSnapshot? snap = null, IReadOnlyList<TariffInfo>? tariffs = null, int? resend = null,
        IReadOnlyList<AppInfo>? apps = null) =>
        new(r.Id, true, 200, null, snap ?? Current, tariffs, null, resend, apps);

    public IpcResponse Fail(IpcRequest r, int status, string error) => new(r.Id, false, status, error, Current);

    public IEnumerable<IpcRequest> Ops(string op) => Sent.Where(r => r.Op == op);
}
