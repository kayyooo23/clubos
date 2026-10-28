using ClubOS.Shared;

namespace ClubOS.Service;

/// <summary>Раз в несколько секунд спрашивает сервер о состоянии ПК (это же heartbeat «ПК на связи») и передаёт исполнителю пришедшие команды.</summary>
public sealed class PollWorker(AgentCore core, AgentConfig cfg, ILogger<PollWorker> log, CommandRunner? commands = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var wasConnected = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await core.RefreshAsync(stoppingToken);
                var connected = core.Snapshot().Connected;
                if (connected != wasConnected)
                    log.LogInformation(connected ? "Связь с сервером восстановлена" : "Нет связи с сервером");
                wasConnected = connected;
                if (commands is not null && core.TakeCommands() is { Count: > 0 } pending)
                    await commands.HandleAsync(pending, core.Snapshot(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) { log.LogError(e, "Сбой опроса сервера"); } // служба не должна падать
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(cfg.PollSeconds, 1)), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
