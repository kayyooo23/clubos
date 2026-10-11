using ClubOS.Shared;

namespace ClubOS.Service.Cleanup;

/// <summary>
/// Решает, КОГДА чистить ПК. Следит за состоянием раз в секунду:
///  • сессия гостя закончилась (остановил сам, кончилось время или деньги, пропала связь): чистим;
///  • сотрудник ушёл с рабочего стола: чистить не нужно, только обновляем «чистый» снимок, чтобы его программы потом не закрылись;
///  • ПК только что включился и чистый: делаем базовый снимок процессов.
/// Пока идёт сессия гостя, чистка не запускается ни при каких условиях (перед началом проверяется ещё раз).
/// </summary>
public sealed class CleanupWorker(AgentCore core, SessionCleaner cleaner, AgentConfig cfg, ILogger<CleanupWorker> log) : BackgroundService
{
    public static CleanupAction Decide(AgentSnapshot? previous, AgentSnapshot current, bool hasBaseline)
    {
        if (current.Mode == PcMode.GuestSession) return CleanupAction.None;
        if (previous?.Mode == PcMode.GuestSession && current.Mode == PcMode.Locked) return CleanupAction.Clean;
        if (previous?.Mode == PcMode.StaffDesktop && current.Mode == PcMode.Locked) return CleanupAction.Rebaseline;
        if (!hasBaseline && current.Connected && current.Mode == PcMode.Locked && current.Account is null) return CleanupAction.Rebaseline;
        return CleanupAction.None;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (cfg.Cleanup == "off") { log.LogInformation("Очистка ПК после сессии выключена (cleanup = off)"); return; }
        if (string.IsNullOrWhiteSpace(cfg.GuestUser))
            log.LogWarning("Очистка ПК после сессии не работает: в agent.json не задан guest_user (Windows-пользователь гостей)");
        else
            log.LogInformation("Очистка ПК после сессии: режим {Mode}, гостевой пользователь {User}", cfg.Cleanup, cfg.GuestUser);

        AgentSnapshot? previous = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = core.Snapshot();
                switch (Decide(previous, now, cleaner.HasBaseline))
                {
                    case CleanupAction.Clean:
                        if (core.Snapshot().Mode != PcMode.GuestSession)      // последняя проверка: новая сессия могла начаться
                            await Task.Run(() => cleaner.Clean("сессия гостя закончилась"), stoppingToken);
                        break;
                    case CleanupAction.Rebaseline:
                        cleaner.TakeBaseline();
                        break;
                }
                previous = now;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) { log.LogError(e, "Сбой очистки ПК"); }       // очистка не должна ронять службу
            try { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}

public enum CleanupAction { None, Clean, Rebaseline }
