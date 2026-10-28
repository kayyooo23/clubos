using System.Text.Json;
using ClubOS.Service.Update;
using ClubOS.Shared;

namespace ClubOS.Service.Sync;

/// <summary>
/// Ведёт участие этого ПК в заданиях синхронизации. Сервер в ответе state присылает задания (Update): по каждому запускается свой исполнитель, пропало задание из ответа
/// (закрыто, отменено) исполнитель останавливается и раздача прекращается. Всё остальное он делает сам, сервер только получает отчёты о ходе работы.
///
/// Эталон: считает список файлов выбранных папок (хеши прежнего подсчёта переиспользуются), сообщает серверу sha256 списка, начинает раздавать файлы и ждёт.
/// Получатель: ждёт, пока у задания появится список, и пока ПК свободен (гостя нет: копирование нагружает диск и сеть); берёт список у любого участника и сверяет его sha256
/// с сообщённым сервером; начинает раздавать уже полученные файлы; копирует; если связь или источники пропали, ждёт и повторяет, а при настоящей неудаче сообщает причину.
/// Пока в ходе копирования ПК занимает гость, копирование ставится на паузу и продолжается, когда ПК освободится. Готовый получатель продолжает раздавать файлы, пока задание идёт.
/// </summary>
public sealed class SyncCoordinator(ServerApi api, HttpClient peerHttp, SyncServer? server, string root, string syncDir, Func<bool> isIdle, Func<string?> advertiseUrl,
    SyncCoordinator.Timing? timing = null, Action<string>? log = null)
{
    public sealed record Timing(TimeSpan ReportEvery, TimeSpan WaitPoll, TimeSpan RetryDelay, int MaxRetries, int BlockSize)
    {
        public static Timing Default { get; } = new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(30), 6, SyncManifest.DefaultBlockSize);
    }

    private sealed class Job
    {
        public required int Id { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public SyncAssignment Current { get; set; } = null!;
        public Task? Task { get; set; }
        public DateTimeOffset LastReport { get; set; }
    }

    private readonly Timing _t = timing ?? Timing.Default;
    private readonly object _gate = new();
    private readonly Dictionary<int, Job> _jobs = [];

    public bool IsRunning(int jobId) { lock (_gate) return _jobs.ContainsKey(jobId); }

    /// <summary>Свежий список заданий из ответа сервера: запустить новые, обновить параметры идущих, остановить пропавшие.</summary>
    public void Update(IReadOnlyList<SyncAssignment> assignments)
    {
        lock (_gate)
        {
            foreach (var a in assignments)
            {
                if (_jobs.TryGetValue(a.Id, out var existing)) { existing.Current = a; continue; }
                var job = new Job { Id = a.Id, Cts = new CancellationTokenSource(), Current = a };
                _jobs[a.Id] = job;
                job.Task = Task.Run(() => RunAsync(job));
            }
            foreach (var id in _jobs.Keys.Where(id => assignments.All(a => a.Id != id)).ToList())
            {
                _jobs[id].Cts.Cancel();
                server?.Stop(id);
                _jobs.Remove(id);
            }
        }
    }

    public async Task StopAllAsync()
    {
        List<Job> all;
        lock (_gate) { all = _jobs.Values.ToList(); _jobs.Clear(); }
        foreach (var job in all) { job.Cts.Cancel(); server?.Stop(job.Id); }
        foreach (var job in all) { try { if (job.Task is not null) await job.Task.ConfigureAwait(false); } catch (Exception) { /* остановлено */ } }
    }

    private SyncAssignment Latest(Job job) { lock (_gate) return job.Current; }

    // ---------- отчёты серверу ----------
    /// <summary>false: задание на сервере уже закрыто (409, 404), работу пора прекращать.</summary>
    private async Task<bool> ReportAsync(Job job, string status, SyncStats? s = null, string? message = null, bool withUrl = false, CancellationToken ct = default)
    {
        var body = new
        {
            status, bytes_done = s?.BytesDone ?? 0, bytes_total = s?.BytesTotal ?? 0, bytes_fetched = s?.BytesFetched ?? 0, bytes_from_peers = s?.BytesFromPeers ?? 0,
            files_done = s?.FilesDone ?? 0, files_total = s?.FilesTotal ?? 0, message = Short(message), serve_url = withUrl ? advertiseUrl() : null,
        };
        var r = await api.PostAsync<JsonElement>($"/api/agent/sync/{job.Id}/progress", body, null, ct).ConfigureAwait(false);
        if (r.Status is 404 or 409) { job.Cts.Cancel(); return false; }
        return true;
    }

    private static string? Short(string? m) => m is null ? null : m.Length <= 190 ? m : m[..190];

    /// <summary>Отчёт о ходе копирования не чаще раза в ReportEvery (вызывается из потоков копирования).</summary>
    private void Throttled(Job job, SyncStats s, bool serving)
    {
        var now = DateTimeOffset.UtcNow;
        lock (job)
        {
            if (now - job.LastReport < _t.ReportEvery) return;
            job.LastReport = now;
        }
        _ = ReportAsync(job, "syncing", s, null, serving);
    }

    // ---------- исполнитель ----------
    private async Task RunAsync(Job job)
    {
        var ct = job.Cts.Token;
        try
        {
            if (Latest(job).Role == "source") await RunSourceAsync(job, ct).ConfigureAwait(false);
            else await RunTargetAsync(job, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* задание закрыто или служба останавливается */ }
        catch (Exception e)
        {
            log?.Invoke($"Синхронизация {job.Id}: непредвиденный сбой {e.GetType().Name}: {e.Message}");
            try { await ReportAsync(job, "failed", null, $"Непредвиденный сбой: {e.GetType().Name}").ConfigureAwait(false); } catch (Exception) { /* связи нет */ }
        }
    }

    private string CachePath => Path.Combine(syncDir, "last-manifest.json");

    private Dictionary<string, ManifestFile>? LoadPrevious()
    {
        try
        {
            var m = File.Exists(CachePath) ? SyncManifest.Parse(File.ReadAllBytes(CachePath), out _) : null;
            return m is null || m.BlockSize != _t.BlockSize ? null : m.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private void SavePrevious(SyncManifest manifest)
    {
        try { Directory.CreateDirectory(syncDir); File.WriteAllBytes(CachePath, manifest.ToBytes()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* кэш необязателен */ }
    }

    private async Task RunSourceAsync(Job job, CancellationToken ct)
    {
        var a = Latest(job);
        if (server is null || !server.Started || advertiseUrl() is null)
        {
            await ReportAsync(job, "failed", null, "Этот ПК не может раздавать файлы по сети: порт не занят или не найден адрес в локальной сети", ct: ct).ConfigureAwait(false);
            return;
        }
        if (!await ReportAsync(job, "scanning", null, "Считаются файлы игры", ct: ct).ConfigureAwait(false)) return;
        var builder = new ManifestBuilder(root, _t.BlockSize);
        var lastShown = DateTimeOffset.MinValue;
        var result = await builder.BuildAsync(a.Paths, LoadPrevious(), (done, total) =>
        {
            if (DateTimeOffset.UtcNow - lastShown < _t.ReportEvery) return;
            lastShown = DateTimeOffset.UtcNow;
            _ = ReportAsync(job, "scanning", new SyncStats(done, total, 0, 0, 0, 0), "Считаются файлы игры");
        }, ct).ConfigureAwait(false);
        if (result.Manifest is null)
        {
            await ReportAsync(job, "failed", null, result.Error, ct: ct).ConfigureAwait(false);
            return;
        }
        var bytes = result.Manifest.ToBytes();
        var digest = SyncManifest.Digest(bytes);
        a = Latest(job);
        if (a.ManifestSha256 is not null && !a.ManifestSha256.Equals(digest, StringComparison.OrdinalIgnoreCase))
        {
            await ReportAsync(job, "failed", null, "Файлы на эталонном ПК изменились после того, как был подготовлен список", ct: ct).ConfigureAwait(false);
            return;
        }
        SavePrevious(result.Manifest);
        if (a.ManifestSha256 is null)
        {
            var posted = await api.PostAsync<JsonElement>($"/api/agent/sync/{job.Id}/manifest",
                new { sha256 = digest, files = result.Manifest.Files.Count, bytes = result.Manifest.TotalBytes }, null, ct).ConfigureAwait(false);
            if (!posted.Ok)
            {
                if (posted.Status is 404 or 409) job.Cts.Cancel();
                else await ReportAsync(job, "failed", null, $"Сервер не принял список файлов: {posted.Error}", ct: ct).ConfigureAwait(false);
                return;
            }
        }
        server.Serve(job.Id, a.Token, result.Manifest, bytes);
        var total = new SyncStats(result.Manifest.TotalBytes, result.Manifest.TotalBytes, 0, 0, result.Manifest.Files.Count, result.Manifest.Files.Count);
        await ReportAsync(job, "ready", total, $"Файлов: {result.Manifest.Files.Count}", withUrl: true, ct).ConfigureAwait(false);
        log?.Invoke($"Синхронизация {job.Id}: эталон готов, файлов {result.Manifest.Files.Count}");
        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);                      // раздаём, пока задание идёт
    }

    private async Task RunTargetAsync(Job job, CancellationToken ct)
    {
        var downloader = new SyncDownloader(peerHttp, root);
        var attempts = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var a = Latest(job);
            if (a.ManifestSha256 is null)
            {
                if (!await ReportAsync(job, "waiting", null, "Ждём, пока эталонный ПК подготовит список файлов", ct: ct).ConfigureAwait(false)) return;
                await Task.Delay(_t.WaitPoll, ct).ConfigureAwait(false);
                continue;
            }
            if (!isIdle())
            {
                if (!await ReportAsync(job, "waiting", null, "Ждём, когда ПК освободится: за ним гость", ct: ct).ConfigureAwait(false)) return;
                await Task.Delay(_t.WaitPoll, ct).ConfigureAwait(false);
                continue;
            }
            var (manifest, bytes, error) = await downloader.FetchManifestAsync(job.Id, a.Token, a.ManifestSha256, a.Peers ?? [], ct).ConfigureAwait(false);
            if (manifest is null)
            {
                if (++attempts >= _t.MaxRetries * 4) { await ReportAsync(job, "failed", null, error, ct: ct).ConfigureAwait(false); return; }   // источник так и не нашёлся
                if (!await ReportAsync(job, "waiting", null, error, ct: ct).ConfigureAwait(false)) return;
                await Task.Delay(_t.RetryDelay, ct).ConfigureAwait(false);
                continue;
            }
            var serving = server is { Started: true } && advertiseUrl() is not null;
            if (serving) server!.Serve(job.Id, a.Token, manifest, bytes!);                // уже лежащие на месте файлы можно раздавать сразу
            if (!await ReportAsync(job, "syncing", new SyncStats(0, manifest.TotalBytes, 0, 0, 0, manifest.Files.Count), "Копирование", serving, ct).ConfigureAwait(false)) return;

            var paused = false;
            Func<CancellationToken, Task> gate = async token =>
            {
                while (!isIdle())                                                         // гость сел за ПК: ставим на паузу, пока он не уйдёт
                {
                    if (!paused) { paused = true; _ = ReportAsync(job, "waiting", null, "Пауза: за ПК гость"); }
                    await Task.Delay(_t.WaitPoll, token).ConfigureAwait(false);
                }
                paused = false;
            };
            var outcome = await downloader.RunAsync(job.Id, a.Token, manifest, a.Paths, a.DeleteExtra, () => Latest(job).Peers ?? [], s => Throttled(job, s, serving), gate, ct).ConfigureAwait(false);
            if (outcome.Ok)
            {
                await ReportAsync(job, "ready", outcome.Stats, null, serving, ct).ConfigureAwait(false);
                log?.Invoke($"Синхронизация {job.Id}: готово, скачано {outcome.Stats.BytesFetched} байт");
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);               // раздаём другим, пока задание идёт
                return;
            }
            var transient = outcome.Error is not null && (outcome.Error.StartsWith("Не удалось получить", StringComparison.Ordinal) || outcome.Error.StartsWith("Нет участников", StringComparison.Ordinal));
            if (!transient || ++attempts >= _t.MaxRetries)
            {
                await ReportAsync(job, "failed", outcome.Stats, outcome.Error, ct: ct).ConfigureAwait(false);
                return;
            }
            if (!await ReportAsync(job, "waiting", outcome.Stats, $"{outcome.Error}. Повторим позже", ct: ct).ConfigureAwait(false)) return;
            await Task.Delay(_t.RetryDelay, ct).ConfigureAwait(false);
        }
    }
}

/// <summary>Раз в пару секунд передаёт координатору свежий список заданий из ответа сервера и сама раздача: занимает порт при старте.</summary>
public sealed class SyncWorker(AgentCore core, SyncCoordinator coordinator, SyncServer? server, ILogger<SyncWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (server is not null && !server.TryStart(out var error)) log.LogWarning("{Message}", error);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { if (core.Snapshot().Connected) coordinator.Update(core.SyncAssignments); }
            catch (Exception e) { log.LogError(e, "Сбой синхронизации игр"); }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        await coordinator.StopAllAsync().ConfigureAwait(false);
        server?.Dispose();
    }
}

/// <summary>Присылает серверу список игр из игровой папки: при запуске, раз в 6 часов и когда администратор просил обновить. Сбой не опасен: повторим позже.</summary>
public sealed class GamesReporter(AgentCore core, ServerApi api, string root, TimeProvider time, ILogger<GamesReporter> log) : BackgroundService
{
    public static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(40);
    public static readonly TimeSpan Every = TimeSpan.FromHours(6);
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);
    private DateTimeOffset _next;

    public async Task<bool> TickAsync(CancellationToken ct)
    {
        if (!api.HasKey) return false;
        var requested = core.GamesRequested;
        var now = time.GetUtcNow();
        if (!requested && now < _next) return false;
        var entries = await Task.Run(() => GamesInventory.Scan(root, ct), ct).ConfigureAwait(false);
        var body = new { root, entries = entries.Select(e => new { path = e.Path, name = e.Name, kind = e.Kind, size_bytes = e.SizeBytes, files = e.Files }).ToList() };
        var r = await api.PostAsync<JsonElement>("/api/agent/games", body, null, ct).ConfigureAwait(false);
        _next = now + (r.Ok ? Every : RetryAfter);
        if (r.Ok) { core.GamesReported(); log.LogInformation("Отправлен список игр: {Count}", entries.Count); }
        else log.LogWarning("Не удалось отправить список игр ({Status}): {Error}. Повторим через {Minutes} мин", r.Status, r.Error, RetryAfter.TotalMinutes);
        return r.Ok;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(FirstDelay, time, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) { log.LogError(e, "Сбой отправки списка игр"); }
            try { await Task.Delay(TimeSpan.FromSeconds(3), time, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }
}
