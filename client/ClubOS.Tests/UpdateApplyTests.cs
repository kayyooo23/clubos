using System.Text.Json;
using ClubOS.Service;
using ClubOS.Service.Update;
using ClubOS.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ClubOS.Tests;

/// <summary>Поддельные «службы Windows»: записывают, что с ними делали, и по команде подыгрывают новой версии (пишут отметку «работаю»).</summary>
public sealed class FakeHost : IUpdateHost
{
    public List<string> Calls { get; } = [];
    public string ServiceBinary { get; private set; } = "";
    public HashSet<string> FailOn { get; } = [];
    public Action<string>? OnServiceStart { get; set; }

    private Task Step(string name, string? detail = null)
    {
        Calls.Add(detail is null ? name : $"{name} {detail}");
        if (FailOn.Contains(name)) throw new InvalidOperationException($"{name} не удалось (тест)");
        return Task.CompletedTask;
    }

    public Task SetServiceBinaryAsync(string serviceName, string exePath, CancellationToken ct) { ServiceBinary = exePath; return Step("SetService", exePath); }
    public Task SetOverlayCommandAsync(string taskName, string exePath, CancellationToken ct) => Step("SetOverlay", exePath);
    public Task StopOverlayAsync(CancellationToken ct) => Step("StopOverlay");
    public Task StopServiceAsync(string serviceName, CancellationToken ct) => Step("StopService");
    public Task StartServiceAsync(string serviceName, CancellationToken ct)
    {
        var t = Step("StartService");
        OnServiceStart?.Invoke(ServiceBinary);
        return t;
    }
    public Task StartOverlayAsync(string taskName, CancellationToken ct) => Step("StartOverlay");
}

public abstract class UpdateDirTests : IDisposable
{
    protected readonly string Root = Path.Combine(Path.GetTempPath(), "clubos-test-apply-" + Guid.NewGuid().ToString("N"));
    protected string Updates => Path.Combine(Root, "updates");
    protected string OldDir => Path.Combine(Root, "versions", "1.0.0");
    protected string NewDir => Path.Combine(Root, "versions", "1.1.0");
    protected string Health => Path.Combine(Updates, "health.json");
    protected UpdateStateStore State => new(Path.Combine(Updates, "update-state.json"));
    protected BadVersions Bad => new(Path.Combine(Updates, "bad-versions.json"));

    protected UpdateDirTests()
    {
        Directory.CreateDirectory(Updates);
        foreach (var dir in new[] { OldDir, NewDir })
        {
            Directory.CreateDirectory(Path.Combine(dir, "Service"));
            Directory.CreateDirectory(Path.Combine(dir, "Overlay"));
            File.WriteAllText(InstallLayout.ServiceExe(dir), "svc");
            File.WriteAllText(InstallLayout.OverlayExe(dir), "ovl");
        }
    }

    public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }

    protected UpdatePlan Plan(int timeout = 5) => new("1.0.0", "1.1.0", OldDir, NewDir, Health, timeout);

    protected static void Healthy(string version) { }
}

public class UpdateApplierTests : UpdateDirTests
{
    private UpdateApplier Applier(IUpdateHost host) => new(host, State, Bad, null, null, TimeSpan.FromMilliseconds(20));

    private void NewVersionWorks(FakeHost host) =>
        host.OnServiceStart = binary => { if (binary.Contains("1.1.0")) HealthFile.Write(Health, "1.1.0", DateTimeOffset.UtcNow); };

    [Fact]
    public async Task A_new_version_that_reports_in_is_kept_and_the_steps_run_in_a_safe_order()
    {
        var host = new FakeHost();
        NewVersionWorks(host);
        var result = await Applier(host).RunAsync(Plan());
        Assert.Equal(UpdateApplier.Switched, result);
        Assert.Equal(["SetService " + InstallLayout.ServiceExe(NewDir), "SetOverlay " + InstallLayout.OverlayExe(NewDir), "StopOverlay", "StopService", "StartService", "StartOverlay"], host.Calls);
        Assert.Equal("switching", State.Load()!.State);                                // «ok» записывает сама новая версия, не помощник
        Assert.False(Bad.IsBad("1.1.0"));
    }

    [Fact]
    public async Task A_new_version_that_never_reports_in_is_rolled_back_to_the_old_one()
    {
        var host = new FakeHost();                                                     // новая версия «запускается», но на связь не выходит
        var result = await Applier(host).RunAsync(Plan(timeout: 1));
        Assert.Equal(UpdateApplier.RolledBack, result);
        Assert.Equal(["StopOverlay", "StopService", "SetService " + InstallLayout.ServiceExe(OldDir), "SetOverlay " + InstallLayout.OverlayExe(OldDir), "StartService", "StartOverlay"],
            host.Calls.Skip(6));                                                       // после переключения: откат в обратную сторону и запуск прежней версии
        Assert.Equal(InstallLayout.ServiceExe(OldDir), host.ServiceBinary);
        var state = State.Load()!;
        Assert.Equal("rolled_back", state.State);
        Assert.Contains("не вышла на связь", state.Message);
        Assert.True(Bad.IsBad("1.1.0"));
        Assert.True(File.Exists(InstallLayout.ServiceExe(OldDir)) && File.Exists(InstallLayout.ServiceExe(NewDir)));   // ни одна версия не удалена
    }

    [Theory]
    [InlineData("SetService")]
    [InlineData("SetOverlay")]
    [InlineData("StopService")]
    [InlineData("StartService")]
    public async Task A_failing_step_rolls_back_at_once_without_waiting(string failing)
    {
        var host = new FakeHost();
        host.FailOn.Add(failing);
        NewVersionWorks(host);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        host.OnServiceStart = null;
        var result = await Applier(host).RunAsync(Plan(timeout: 30));
        // шаг, на котором упали, в откате повторяется и падает снова: откат доводится до конца, но итог честно «не вышло»
        Assert.True(result is UpdateApplier.RolledBack or UpdateApplier.Broken);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Equal(InstallLayout.ServiceExe(OldDir), host.ServiceBinary == "" ? InstallLayout.ServiceExe(OldDir) : host.ServiceBinary);
        Assert.Contains(host.Calls, c => c == "StartOverlay" || c == "StartService");
        Assert.True(Bad.IsBad("1.1.0"));
    }

    [Fact]
    public async Task The_reason_for_the_admin_is_one_short_line_even_when_windows_answers_with_many()
    {
        var host = new MultilineHost();
        await Applier(host).RunAsync(Plan(timeout: 30));
        var message = State.Load()!.Message!;
        Assert.DoesNotContain('\n', message);
        Assert.DoesNotContain('\r', message);
        Assert.DoesNotContain("  ", message);
        Assert.True(message.Length <= 200);
        Assert.StartsWith("Не удалось переключить версию:", message);
    }

    private sealed class MultilineHost : IUpdateHost
    {
        public Task SetServiceBinaryAsync(string serviceName, string exePath, CancellationToken ct) =>
            throw new InvalidOperationException("sc.exe config: код 1060\r\n\r\n[SC] OpenService: ошибка: 1060:\r\n\r\nУказанная служба не установлена." + new string('x', 400));
        public Task SetOverlayCommandAsync(string taskName, string exePath, CancellationToken ct) => Task.CompletedTask;
        public Task StopOverlayAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopServiceAsync(string serviceName, CancellationToken ct) => Task.CompletedTask;
        public Task StartServiceAsync(string serviceName, CancellationToken ct) => Task.CompletedTask;
        public Task StartOverlayAsync(string taskName, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task A_rollback_that_itself_fails_is_reported_as_failed_not_as_success()
    {
        var host = new FakeHost();
        host.FailOn.Add("StartService");                                               // и новая, и прежняя версии не запускаются
        var result = await Applier(host).RunAsync(Plan(timeout: 30));
        Assert.Equal(UpdateApplier.Broken, result);
        Assert.Equal("failed", State.Load()!.State);
        Assert.Contains(host.Calls, c => c == "StartOverlay");                         // остальные шаги отката выполнены, несмотря на сбой
    }

    [Fact]
    public async Task An_old_health_mark_does_not_count_as_the_new_versions_confirmation()
    {
        HealthFile.Write(Health, "1.1.0", DateTimeOffset.UtcNow - TimeSpan.FromHours(1));     // осталась от прошлой попытки
        var host = new FakeHost();
        var result = await Applier(host).RunAsync(Plan(timeout: 1));
        Assert.Equal(UpdateApplier.RolledBack, result);
    }

    [Fact]
    public async Task A_bad_plan_is_refused_before_anything_is_touched()
    {
        var host = new FakeHost();
        var cases = new[]
        {
            Plan() with { ToVersion = "1.0.0" },                                       // не новее
            Plan() with { ToVersion = "1.1.0; calc" },
            Plan() with { ServiceName = "ClubOSAgent\" & calc & \"" },                  // подмена в командной строке sc.exe
            Plan() with { TaskName = "x\" /RU SYSTEM" },
            Plan() with { ToDir = Path.Combine(Root, "versions", "9.9.9") },           // такой версии нет на диске
        };
        foreach (var plan in cases) Assert.Equal(UpdateApplier.Refused, await Applier(host).RunAsync(plan));
        Assert.Empty(host.Calls);                                                      // ни одного системного действия
        var state = State.Load()!;
        Assert.Equal("failed", state.State);                                           // отказ запомнен: служба сообщит о нём серверу и не будет пробовать снова и снова
        Assert.Contains("отклонено", state.Message);
        Assert.True(Bad.IsBad("1.1.0"));
    }

    [Fact]
    public async Task A_plan_with_garbage_versions_is_refused_without_recording_anything()
    {
        var host = new FakeHost();
        Assert.Equal(UpdateApplier.Refused, await Applier(host).RunAsync(Plan() with { ToVersion = "1.1.0; calc" }));
        Assert.Equal(UpdateApplier.Refused, await Applier(host).RunAsync(Plan() with { ToVersion = "1.0.0" }));       // не новее: нечего запоминать
        Assert.Empty(host.Calls);
        Assert.Null(State.Load());
        Assert.False(Bad.IsBad("1.0.0"));                                              // текущую версию плохой не объявляем
    }
}

public class UpdateReconcilerTests : UpdateDirTests
{
    private readonly FakeServer _server = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));

    private UpdateReconciler Reconciler(string current)
    {
        var api = new ServerApi(new HttpClient(_server) { BaseAddress = new Uri("http://club.test") }, FakeServer.PcKey);
        return new UpdateReconciler(api, State, Bad, new InstallLayout(Path.Combine(Root, "versions"), current, current == "1.1.0" ? NewDir : OldDir), Health, current, _time);
    }

    private void Switching(long? at = null) => State.Save(new UpdateState("switching", "1.0.0", "1.1.0", at ?? _time.GetUtcNow().ToUnixTimeSeconds()));

    private (string Status, string? Message) Reported(int index = 0)
    {
        using var doc = JsonDocument.Parse(_server.To("/api/agent/update/result").ElementAt(index).Body);
        return (doc.RootElement.GetProperty("status").GetString()!, doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null);
    }

    [Fact]
    public async Task The_new_version_confirms_itself_tells_the_server_and_removes_stale_versions()
    {
        Directory.CreateDirectory(Path.Combine(Root, "versions", "0.9.0"));
        Directory.CreateDirectory(Path.Combine(Root, "versions", "1.2.0.tmp"));
        Switching();
        _server.On("POST", "/api/agent/update/result", 200, Samples.Ok);
        await Reconciler("1.1.0").ReconcileAsync();
        Assert.True(HealthFile.IsHealthy(Health, "1.1.0", _time.GetUtcNow()));          // отметка, которую ждёт помощник
        Assert.Equal("ok", Reported().Status);
        var state = State.Load()!;
        Assert.Equal("ok", state.State);
        Assert.True(state.Reported);
        Assert.True(Directory.Exists(OldDir) && Directory.Exists(NewDir));              // текущая и предыдущая (для отката) остались
        Assert.False(Directory.Exists(Path.Combine(Root, "versions", "0.9.0")));
        Assert.False(Directory.Exists(Path.Combine(Root, "versions", "1.2.0.tmp")));
        _server.Requests.Clear();
        await Reconciler("1.1.0").ReconcileAsync();
        Assert.Empty(_server.Requests);                                                 // повторно не сообщает
    }

    [Fact]
    public async Task A_report_that_did_not_reach_the_server_is_repeated_until_it_does()
    {
        Switching();
        _server.Down = true;
        await Reconciler("1.1.0").ReconcileAsync();
        Assert.False(State.Load()!.Reported);
        Assert.True(HealthFile.IsHealthy(Health, "1.1.0", _time.GetUtcNow()));          // но новая версия работу уже подтвердила
        _server.Down = false;
        _server.On("POST", "/api/agent/update/result", 200, Samples.Ok);
        await Reconciler("1.1.0").ReconcileAsync();
        Assert.True(State.Load()!.Reported);
        Assert.Equal("ok", Reported().Status);
    }

    [Fact]
    public async Task After_a_rollback_the_old_version_tells_the_server_why_once()
    {
        State.Save(new UpdateState("rolled_back", "1.0.0", "1.1.0", _time.GetUtcNow().ToUnixTimeSeconds(), "Версия 1.1.0 не вышла на связь за 180 с"));
        _server.On("POST", "/api/agent/update/result", 200, Samples.Ok);
        await Reconciler("1.0.0").ReconcileAsync();
        var (status, message) = Reported();
        Assert.Equal("rolled_back", status);
        Assert.Contains("не вышла на связь", message);
        Assert.True(State.Load()!.Reported);
        await Reconciler("1.0.0").ReconcileAsync();
        Assert.Single(_server.To("/api/agent/update/result"));
    }

    [Fact]
    public async Task A_switch_that_never_finished_is_written_off_after_a_while_but_not_at_once()
    {
        Switching();
        _server.On("POST", "/api/agent/update/result", 200, Samples.Ok);
        await Reconciler("1.0.0").ReconcileAsync();                                     // помощник, возможно, ещё работает
        Assert.Empty(_server.Requests);
        Assert.Equal("switching", State.Load()!.State);
        _time.Advance(UpdateReconciler.StaleAfter + TimeSpan.FromSeconds(5));
        await Reconciler("1.0.0").ReconcileAsync();
        Assert.Equal("failed", Reported().Status);
        Assert.True(Bad.IsBad("1.1.0"));
    }

    [Fact]
    public async Task Without_an_update_in_progress_nothing_happens_and_a_final_refusal_does_not_loop()
    {
        await Reconciler("1.0.0").ReconcileAsync();
        Assert.Empty(_server.Requests);
        Switching();
        _server.On("POST", "/api/agent/update/result", 422, Samples.Error("Версия не найдена"));
        await Reconciler("1.1.0").ReconcileAsync();
        Assert.True(State.Load()!.Reported);                                            // 4xx: сервер не примет и повтор
    }
}

public class UpdateWorkerTests : UpdateDirTests
{
    private sealed class Launcher : IApplierLauncher
    {
        public List<string> Launched { get; } = [];
        public void Launch(string planPath) => Launched.Add(planPath);
    }

    private readonly FakeServer _server = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));
    private readonly System.Security.Cryptography.ECDsa _key = ReleaseSigning.GenerateKey();
    private readonly Launcher _launcher = new();

    private (UpdateWorker Worker, AgentCore Core) Create(bool enabled = true)
    {
        var api = new ServerApi(new HttpClient(_server) { BaseAddress = new Uri("http://club.test") }, FakeServer.PcKey);
        var layout = new InstallLayout(Path.Combine(Root, "versions"), "1.0.0", OldDir);
        var installer = new UpdateInstaller(api, _key, layout, Path.Combine(Updates, "downloads"), Bad, new PackageValidator(_ => "1.1.0"), _ => long.MaxValue / 8);
        var store = new MemoryTokenStore();
        store.Save("guest-token");                                                                       // как в жизни: гость вошёл через оверлей, и у службы есть его токен
        var core = new AgentCore(api, new AgentConfig("http://club.test", FakeServer.PcKey), _time, store, new AdImageCache(api, Path.Combine(Root, "ads")), new CoverCache(api, Path.Combine(Root, "covers")));
        return (new UpdateWorker(core, api, enabled ? installer : null, null, _launcher, enabled ? layout : null, Updates, "1.0.0", _time, NullLogger<UpdateWorker>.Instance), core);
    }

    private (byte[] Zip, string Json) Release(string version = "1.1.0", System.Security.Cryptography.ECDsa? signer = null)
    {
        var zip = SafeUnzipTests.Zip(("Service/ClubOS.Service.exe", "s"), ("Service/ClubOS.Service.dll", "s"), ("Overlay/ClubOS.Overlay.exe", "o"), ("Overlay/ClubOS.Overlay.dll", "o"), ("version.txt", version));
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(zip)).ToLowerInvariant();
        var sig = ReleaseSigning.Sign(signer ?? _key, version, sha, zip.Length);
        return (zip, $$$"""{"update":{"id":5,"version":"{{{version}}}","sha256":"{{{sha}}}","size":{{{zip.Length}}},"signature":"{{{sig}}}","notes":"n","url":"/api/agent/update/package/5"}}""");
    }

    private async Task Idle(AgentCore core, string state = Samples.Locked)
    {
        _server.On("GET", "/api/agent/state", 200, state);
        await core.RefreshAsync();
    }

    [Fact]
    public async Task An_idle_pc_downloads_prepares_and_hands_over_to_the_helper()
    {
        var (worker, core) = Create();
        await Idle(core);
        var (zip, offer) = Release();
        _server.On("GET", "/api/agent/update", 200, offer).OnBytes("GET", "/api/agent/update/package/5", 200, zip);
        Assert.Equal(UpdateWorker.Outcome.Launched, await worker.CheckOnceAsync(default));
        Assert.Equal("1.0.0", System.Web.HttpUtility.ParseQueryString(_server.To("/api/agent/update").Single().Query)["current"]);
        var planPath = Assert.Single(_launcher.Launched);
        var plan = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(planPath), Json.Options)!;
        Assert.Equal("1.0.0", plan.FromVersion);
        Assert.Equal("1.1.0", plan.ToVersion);
        Assert.Equal(NewDir, plan.ToDir);
        Assert.True(File.Exists(InstallLayout.ServiceExe(NewDir)));
    }

    [Fact]
    public async Task A_pc_in_use_is_never_touched_and_downloads_nothing()
    {
        var (worker, core) = Create();
        _server.On("GET", "/api/agent/update", 200, Release().Json);
        Assert.Equal(UpdateWorker.Outcome.Busy, await worker.CheckOnceAsync(default));          // связи ещё не было
        foreach (var busy in new[] { Samples.Cabinet, Samples.Playing, Samples.Staff })
        {
            await Idle(core, busy);
            Assert.Equal(UpdateWorker.Outcome.Busy, await worker.CheckOnceAsync(default));
        }
        Assert.Empty(_server.To("/api/agent/update"));
        Assert.Empty(_launcher.Launched);
    }

    [Fact]
    public async Task A_pc_in_the_middle_of_a_game_sync_is_not_restarted_for_an_update()
    {
        var (worker, core) = Create();
        _server.On("GET", "/api/agent/update", 200, Release().Json);
        await Idle(core, """{"mode":"locked","account":null,"session":null,"sync":[{"id":1,"role":"target","status":"syncing","token":"t","paths":["Games/Dota"],"delete_extra":false}]}""");
        Assert.Single(core.SyncAssignments);
        Assert.Equal(UpdateWorker.Outcome.Busy, await worker.CheckOnceAsync(default));
        Assert.Empty(_server.To("/api/agent/update"));
        await Idle(core);                                                                // копирование закончилось: задания в ответе нет
        Assert.Empty(core.SyncAssignments);
        var (zip, offer) = Release();
        _server.On("GET", "/api/agent/update", 200, offer).OnBytes("GET", "/api/agent/update/package/5", 200, zip);
        Assert.Equal(UpdateWorker.Outcome.Launched, await worker.CheckOnceAsync(default));
    }

    [Fact]
    public async Task Only_a_pc_on_the_lock_screen_with_nobody_in_counts_as_idle()
    {
        Assert.True(UpdateWorker.IsIdle(AgentSnapshot.Booting with { Connected = true }));
        Assert.False(UpdateWorker.IsIdle(AgentSnapshot.Booting));                       // нет связи
        Assert.False(UpdateWorker.IsIdle(AgentSnapshot.Booting with { Connected = true, Mode = PcMode.GuestSession }));
        Assert.False(UpdateWorker.IsIdle(AgentSnapshot.Booting with { Connected = true, Account = new AccountInfo(1, "guest", "+7 ***", 10) }));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task No_offer_or_a_server_error_means_no_update()
    {
        var (worker, core) = Create();
        await Idle(core);
        _server.On("GET", "/api/agent/update", 200, """{"update":null}""");
        Assert.Equal(UpdateWorker.Outcome.NoUpdate, await worker.CheckOnceAsync(default));
        _server.On("GET", "/api/agent/update", 500, Samples.Error("сбой"));
        Assert.Equal(UpdateWorker.Outcome.NoUpdate, await worker.CheckOnceAsync(default));
        Assert.Empty(_launcher.Launched);
    }

    [Fact]
    public async Task Without_the_machinery_the_worker_is_disabled()
    {
        var (worker, core) = Create(enabled: false);
        await Idle(core);
        Assert.Equal(UpdateWorker.Outcome.Disabled, await worker.CheckOnceAsync(default));
        Assert.Empty(_server.Requests.Where(r => r.Path.StartsWith("/api/agent/update")));
    }

    [Fact]
    public async Task A_forged_release_is_rejected_reported_once_and_never_started()
    {
        var (worker, core) = Create();
        await Idle(core);
        using var attacker = ReleaseSigning.GenerateKey();
        var (zip, offer) = Release(signer: attacker);
        _server.On("GET", "/api/agent/update", 200, offer).OnBytes("GET", "/api/agent/update/package/5", 200, zip)
               .On("POST", "/api/agent/update/result", 200, Samples.Ok);
        Assert.Equal(UpdateWorker.Outcome.Failed, await worker.CheckOnceAsync(default));
        Assert.Empty(_server.To("/api/agent/update/package/5"));
        Assert.Empty(_launcher.Launched);
        var report = _server.To("/api/agent/update/result").Single();
        Assert.Contains("\"status\":\"failed\"", report.Body);
        Assert.Equal(UpdateWorker.Outcome.Failed, await worker.CheckOnceAsync(default));    // та же версия снова: не пытаемся и не повторяем отчёт
        Assert.Single(_server.To("/api/agent/update/result"));
    }
}
