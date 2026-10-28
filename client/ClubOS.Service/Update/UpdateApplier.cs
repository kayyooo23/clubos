using System.Diagnostics;
using System.ServiceProcess;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClubOS.Shared;

namespace ClubOS.Service.Update;

/// <summary>Системные действия, нужные для смены версии. Отдельный интерфейс, чтобы порядок шагов и откат проверялись тестами без настоящей службы.</summary>
public interface IUpdateHost
{
    Task SetServiceBinaryAsync(string serviceName, string exePath, CancellationToken ct);
    Task SetOverlayCommandAsync(string taskName, string exePath, CancellationToken ct);
    Task StopOverlayAsync(CancellationToken ct);
    Task StopServiceAsync(string serviceName, CancellationToken ct);
    Task StartServiceAsync(string serviceName, CancellationToken ct);
    Task StartOverlayAsync(string taskName, CancellationToken ct);
}

/// <summary>
/// Переключает версию: новая версия уже лежит в своей папке (UpdateInstaller). Помощник запускается службой, но живёт отдельно: службу он остановит,
/// а сам продолжит, потому что работает из папки прежней версии, которую не трогает. Шаги:
///  1) записать состояние «переключаем»; 2) указать службе и задаче оверлея путь к новой версии; 3) остановить оверлей и службу, запустить службу и оверлей;
///  4) ждать отметку «новая версия вышла на связь» (HealthFile); не дождались за TimeoutSeconds, или любой шаг не удался:
///  5) откат: вернуть прежние пути и запустить прежнюю версию, запомнить новую как плохую.
/// Прежняя версия при этом нигде не затирается, поэтому откат возможен всегда, даже если новая не запускается вовсе.
/// </summary>
public sealed partial class UpdateApplier(IUpdateHost host, UpdateStateStore state, BadVersions bad, TimeProvider? time = null, Action<string>? log = null, TimeSpan? poll = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly TimeSpan _poll = poll ?? TimeSpan.FromSeconds(2);

    [GeneratedRegex(@"^[A-Za-z0-9 ._-]{1,60}$")]
    private static partial Regex NamePattern();

    public const int Switched = 0, RolledBack = 2, Broken = 3, Refused = 4;

    public async Task<int> RunAsync(UpdatePlan plan, CancellationToken ct = default)
    {
        var problem = Check(plan);
        if (problem is not null)
        {
            log?.Invoke($"Обновление отклонено: {problem}");
            // Отказ запоминается как неудача этой версии: иначе служба запускала бы помощника с тем же заданием снова и снова. Служба сообщит о ней серверу.
            if (AppVersion.IsValid(plan.FromVersion) && AppVersion.IsValid(plan.ToVersion) && AppVersion.IsNewer(plan.ToVersion, plan.FromVersion))
            {
                bad.Add(plan.ToVersion);
                state.Save(new UpdateState("failed", plan.FromVersion, plan.ToVersion, _time.GetUtcNow().ToUnixTimeSeconds(), Trim($"Задание отклонено: {problem}")));
            }
            return Refused;
        }

        var toService = InstallLayout.ServiceExe(plan.ToDir);
        var toOverlay = InstallLayout.OverlayExe(plan.ToDir);
        var started = _time.GetUtcNow();
        File.Delete(plan.HealthFile);
        state.Save(new UpdateState("switching", plan.FromVersion, plan.ToVersion, started.ToUnixTimeSeconds()));
        log?.Invoke($"Переключаем {plan.FromVersion} -> {plan.ToVersion}");

        var failure = await TrySwitchAsync(plan, toService, toOverlay, ct).ConfigureAwait(false);
        if (failure is null)
        {
            var deadline = started + TimeSpan.FromSeconds(plan.TimeoutSeconds);
            while (!HealthFile.IsHealthy(plan.HealthFile, plan.ToVersion, started))
            {
                if (_time.GetUtcNow() >= deadline) { failure = $"Версия {plan.ToVersion} не вышла на связь за {plan.TimeoutSeconds} с"; break; }
                try { await Task.Delay(_poll, _time, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { failure = "Обновление прервано"; break; }
            }
        }
        if (failure is null)
        {
            log?.Invoke($"Версия {plan.ToVersion} работает");
            return Switched;             // состояние «ok» записывает сама новая версия вместе с отчётом серверу
        }
        return await RollBackAsync(plan, failure).ConfigureAwait(false);
    }

    private string? Check(UpdatePlan plan)
    {
        if (!AppVersion.IsValid(plan.FromVersion) || !AppVersion.IsValid(plan.ToVersion) || !AppVersion.IsNewer(plan.ToVersion, plan.FromVersion)) return "неверные версии в задании";
        if (!NamePattern().IsMatch(plan.ServiceName) || !NamePattern().IsMatch(plan.TaskName)) return "неверное имя службы или задачи";
        foreach (var dir in new[] { plan.FromDir, plan.ToDir })
            if (!File.Exists(InstallLayout.ServiceExe(dir)) || !File.Exists(InstallLayout.OverlayExe(dir))) return $"в {dir} нет службы или оверлея";
        return null;
    }

    private async Task<string?> TrySwitchAsync(UpdatePlan plan, string toService, string toOverlay, CancellationToken ct)
    {
        try
        {
            await host.SetServiceBinaryAsync(plan.ServiceName, toService, ct).ConfigureAwait(false);
            await host.SetOverlayCommandAsync(plan.TaskName, toOverlay, ct).ConfigureAwait(false);
            await host.StopOverlayAsync(ct).ConfigureAwait(false);
            await host.StopServiceAsync(plan.ServiceName, ct).ConfigureAwait(false);
            await host.StartServiceAsync(plan.ServiceName, ct).ConfigureAwait(false);
            await host.StartOverlayAsync(plan.TaskName, ct).ConfigureAwait(false);
            return null;
        }
        catch (Exception e)
        {
            return $"Не удалось переключить версию: {e.Message}";
        }
    }

    private async Task<int> RollBackAsync(UpdatePlan plan, string reason)
    {
        log?.Invoke($"Откат на {plan.FromVersion}: {reason}");
        var fromService = InstallLayout.ServiceExe(plan.FromDir);
        var fromOverlay = InstallLayout.OverlayExe(plan.FromDir);
        var ok = true;
        // Откат делается до конца, даже если отдельный шаг не удался: лучше вернуть хоть что-то работающее, чем остановиться посередине. Токен отмены не используем.
        async Task Step(Func<Task> action)
        {
            try { await action().ConfigureAwait(false); }
            catch (Exception e) { ok = false; log?.Invoke($"Шаг отката не удался: {e.Message}"); }
        }
        await Step(() => host.StopOverlayAsync(default));
        await Step(() => host.StopServiceAsync(plan.ServiceName, default));
        await Step(() => host.SetServiceBinaryAsync(plan.ServiceName, fromService, default));
        await Step(() => host.SetOverlayCommandAsync(plan.TaskName, fromOverlay, default));
        await Step(() => host.StartServiceAsync(plan.ServiceName, default));
        await Step(() => host.StartOverlayAsync(plan.TaskName, default));
        bad.Add(plan.ToVersion);
        state.Save(new UpdateState(ok ? "rolled_back" : "failed", plan.FromVersion, plan.ToVersion, _time.GetUtcNow().ToUnixTimeSeconds(), Trim(reason)));
        return ok ? RolledBack : Broken;
    }

    /// <summary>Сообщение для администратора: ответы sc.exe и schtasks приходят с переводами строк и пустыми строками, в панели нужна одна строка до 200 знаков.</summary>
    private static string Trim(string text)
    {
        var line = Regex.Replace(text.Trim(), @"\s+", " ");
        return line.Length <= 200 ? line : line[..200];
    }
}

/// <summary>Настоящие действия на Windows: служба через диспетчер служб (sc.exe и ServiceController), оверлей через планировщик заданий. Не проверяется юнит-тестами.</summary>
public sealed class WindowsUpdateHost : IUpdateHost
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(60);

    public Task SetServiceBinaryAsync(string serviceName, string exePath, CancellationToken ct) =>
        RunAsync("sc.exe", ["config", serviceName, "binPath=", $"\"{exePath}\""], ct);

    public Task SetOverlayCommandAsync(string taskName, string exePath, CancellationToken ct) =>
        RunAsync("schtasks.exe", ["/Change", "/TN", taskName, "/TR", $"\"{exePath}\" --kiosk"], ct);

    public Task StopOverlayAsync(CancellationToken ct)
    {
        foreach (var p in Process.GetProcessesByName("ClubOS.Overlay"))
        {
            try { p.Kill(true); p.WaitForExit(5000); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { /* уже закрылся */ }
            finally { p.Dispose(); }
        }
        return Task.CompletedTask;
    }

    public Task StopServiceAsync(string serviceName, CancellationToken ct)
    {
        using var service = new ServiceController(serviceName);
        if (service.Status != ServiceControllerStatus.Stopped && service.Status != ServiceControllerStatus.StopPending) service.Stop();
        service.WaitForStatus(ServiceControllerStatus.Stopped, StopTimeout);
        return Task.CompletedTask;
    }

    public Task StartServiceAsync(string serviceName, CancellationToken ct)
    {
        using var service = new ServiceController(serviceName);
        if (service.Status != ServiceControllerStatus.Running && service.Status != ServiceControllerStatus.StartPending) service.Start();
        return Task.CompletedTask;          // «запущена» ещё не значит «работает»: это решает отметка HealthFile
    }

    public Task StartOverlayAsync(string taskName, CancellationToken ct) => RunAsync("schtasks.exe", ["/Run", "/TN", taskName], ct);

    private static async Task RunAsync(string file, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"{file} не запустился");
        var output = p.StandardOutput.ReadToEndAsync(ct);
        var error = p.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        if (p.ExitCode != 0) throw new InvalidOperationException($"{file} {args[0]}: код {p.ExitCode} {(await error).Trim()} {(await output).Trim()}".Trim());
    }
}

/// <summary>Запуск режима «--apply-update план.json» (отдельный процесс из папки прежней версии).</summary>
public static class UpdateApplierEntry
{
    public static async Task<int> RunAsync(string planPath)
    {
        var logFile = Path.Combine(Path.GetDirectoryName(planPath)!, "apply.log");
        void Log(string message)
        {
            try { File.AppendAllText(logFile, $"{DateTimeOffset.Now:u} {message}{Environment.NewLine}"); }
            catch (IOException) { /* журнал необязателен */ }
        }
        UpdatePlan? plan;
        try { plan = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(planPath), Json.Options); }
        catch (Exception e) when (e is IOException or JsonException) { Log($"Задание не прочитано: {e.Message}"); return UpdateApplier.Refused; }
        if (plan is null) return UpdateApplier.Refused;
        var dir = Path.GetDirectoryName(planPath)!;
        var applier = new UpdateApplier(new WindowsUpdateHost(), new UpdateStateStore(Path.Combine(dir, "update-state.json")), new BadVersions(Path.Combine(dir, "bad-versions.json")), null, Log);
        return await applier.RunAsync(plan).ConfigureAwait(false);
    }
}
