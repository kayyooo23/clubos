using System.Security.Cryptography;
using System.Text;
using ClubOS.Shared;

namespace ClubOS.Service;

/// <summary>
/// Присылает на сервер список программ, найденных на этом ПК: при запуске, потом раз в пару часов (если список изменился) и сразу,
/// когда администратор нажал «Обновить список» (сервер сообщает об этом в ответе state). Сбой не опасен: повторим позже.
/// </summary>
public sealed class ProgramsReporter(ServerApi api, AgentCore core, Func<IReadOnlyList<ProgramEntry>> scan, TimeProvider time, ILogger<ProgramsReporter> log) : BackgroundService
{
    public static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan Every = TimeSpan.FromHours(2);
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Look = TimeSpan.FromSeconds(3);

    private string? _lastHash;
    private DateTimeOffset _next;

    public async Task<bool> ReportOnceAsync(IReadOnlyList<ProgramEntry> programs, CancellationToken ct)
    {
        var body = new { programs = programs.Select(p => new { name = p.Name, path = p.Path, icon = p.Icon is null ? null : Convert.ToBase64String(p.Icon) }).ToList() };
        var r = await api.PostAsync<System.Text.Json.JsonElement>("/api/agent/programs", body, null, ct).ConfigureAwait(false);
        return r.Ok;
    }

    /// <summary>Один проход цикла: смотрим, не пора ли собрать и отправить список. Возвращает true, если отправили.</summary>
    public async Task<bool> TickAsync(CancellationToken ct)
    {
        var requested = core.ProgramsRequested;
        var now = time.GetUtcNow();
        if (!requested && now < _next) return false;

        var programs = await Task.Run(scan, ct).ConfigureAwait(false);
        // подпись списка: названия, пути и иконки (новая версия программы приносит другую иконку, и её тоже стоит отправить)
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
            programs.Select(p => $"{p.Name}|{p.Path}|{(p.Icon is null ? "" : Convert.ToHexString(SHA256.HashData(p.Icon)))}")))));
        if (!requested && hash == _lastHash) { _next = now + Every; return false; }   // ничего не изменилось: сервер уже знает

        if (await ReportOnceAsync(programs, ct).ConfigureAwait(false))
        {
            _lastHash = hash;
            _next = now + Every;
            core.ProgramsReported();
            log.LogInformation("Отправлен список программ: {Count}", programs.Count);
            return true;
        }
        _next = now + RetryAfter;
        log.LogWarning("Не удалось отправить список программ, повторим через {Minutes} мин", RetryAfter.TotalMinutes);
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(FirstDelay, time, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        _next = time.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) { _next = time.GetUtcNow() + RetryAfter; log.LogError(e, "Сбой при сборе списка программ"); }
            try { await Task.Delay(Look, time, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }
}
