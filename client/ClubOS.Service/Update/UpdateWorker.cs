using System.Diagnostics;
using System.Text.Json;
using ClubOS.Shared;

namespace ClubOS.Service.Update;

/// <summary>Запуск помощника, который переключает версию. Отдельный интерфейс: тесты не запускают процессы.</summary>
public interface IApplierLauncher
{
    void Launch(string planPath);
}

/// <summary>
/// Помощник это та же программа службы с аргументом «--apply-update план.json». Запускается из exe работающей (прежней) версии, переключение эту папку не затрагивает.
/// Рабочая папка не задаётся: все пути в задании абсолютные, а свой путь у процесса и так есть (у рабочей папки предел длины короче, чем у обычного пути).
/// </summary>
public sealed class ProcessApplierLauncher : IApplierLauncher
{
    public void Launch(string planPath)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("--apply-update");
        psi.ArgumentList.Add(planPath);
        Process.Start(psi)?.Dispose();
    }
}

/// <summary>
/// Раз в несколько минут спрашивает сервер о новой версии и, если ПК свободен, готовит её и запускает переключение. «Свободен» значит экран блокировки
/// без вошедшего гостя и без сессии: во время игры или в кабинете гостя ничего не перезапускается. Проверки разнесены по времени случайной добавкой,
/// чтобы тридцать ПК клуба не обратились к серверу и не качали пакет в одну секунду. Версия подготавливается заранее (скачана и проверена), а переключается,
/// когда ПК освободится.
/// </summary>
public sealed class UpdateWorker(AgentCore core, ServerApi api, UpdateInstaller? installer, UpdateReconciler? reconciler, IApplierLauncher launcher, InstallLayout? layout,
    string updatesDir, string currentVersion, TimeProvider? time, ILogger<UpdateWorker> log, Random? random = null) : BackgroundService
{
    public enum Outcome { Disabled, Busy, NoUpdate, Failed, Launched }

    public static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan FirstCheckAfter = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(20);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Random _random = random ?? Random.Shared;
    private readonly Dictionary<string, DateTimeOffset> _reported = [];

    /// <summary>ПК можно перезапускать: связь с сервером есть, экран блокировки, никто не вошёл.</summary>
    public static bool IsIdle(AgentSnapshot s) => s.Connected && s.Mode == PcMode.Locked && s.Account is null && s.Session is null;

    public async Task<Outcome> CheckOnceAsync(CancellationToken ct)
    {
        if (layout is null || installer is null) return Outcome.Disabled;
        if (!IsIdle(core.Snapshot())) return Outcome.Busy;
        if (core.SyncAssignments.Count > 0) return Outcome.Busy;                           // идёт синхронизация игр: службу посреди копирования не перезапускаем

        var r = await api.GetAsync<UpdateCheckDto>($"/api/agent/update?current={Uri.EscapeDataString(currentVersion)}", null, ct).ConfigureAwait(false);
        if (!r.Ok || r.Value?.Update is not { } offer) return Outcome.NoUpdate;

        var prepared = await installer.PrepareAsync(offer, currentVersion, ct).ConfigureAwait(false);
        if (!prepared.Ok)
        {
            if (prepared.Permanent) await ReportFailureAsync(offer.Version, prepared.Error!, ct).ConfigureAwait(false);
            return Outcome.Failed;
        }
        if (!IsIdle(core.Snapshot())) return Outcome.Busy;                                // пока качали, кто-то сел за ПК: версия готова, переключим позже

        var plan = new UpdatePlan(currentVersion, offer.Version, layout.CurrentDir, prepared.Dir!, Path.Combine(updatesDir, "health.json"));
        var planPath = Path.Combine(updatesDir, "plan.json");
        Directory.CreateDirectory(updatesDir);
        File.WriteAllText(planPath, JsonSerializer.Serialize(plan, Json.Options));
        launcher.Launch(planPath);
        log.LogInformation("Запущено переключение версии {From} -> {To}", currentVersion, offer.Version);
        return Outcome.Launched;
    }

    /// <summary>Сообщает серверу, что версия не принята (не больше раза в несколько часов на версию): владелец увидит это в панели и не будет гадать, почему ПК не обновился.</summary>
    private async Task ReportFailureAsync(string version, string error, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        if (_reported.TryGetValue(version, out var at) && now - at < BadVersions.Remember) return;
        _reported[version] = now;
        await api.PostAsync<JsonElement>("/api/agent/update/result", new { version, status = "failed", message = error }, null, ct).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (layout is null || installer is null)
        {
            log.LogInformation("Автообновление выключено: программа запущена не из папки versions\\<версия> или в неё не вшит ключ подписи");
            return;
        }
        var next = _time.GetUtcNow() + FirstCheckAfter + TimeSpan.FromSeconds(_random.Next(0, 120));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (core.Snapshot().Connected && reconciler is not null) await reconciler.ReconcileAsync(stoppingToken).ConfigureAwait(false);
                if (_time.GetUtcNow() >= next)
                {
                    var outcome = await CheckOnceAsync(stoppingToken).ConfigureAwait(false);
                    next = _time.GetUtcNow() + (outcome == Outcome.Launched ? TimeSpan.FromMinutes(15) : outcome == Outcome.Busy ? TimeSpan.FromMinutes(1) : CheckEvery)
                           + TimeSpan.FromSeconds(_random.Next(0, 60));
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) { log.LogError(e, "Сбой проверки обновления"); }
            try { await Task.Delay(Tick, _time, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }
}
