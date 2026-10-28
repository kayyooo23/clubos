using System.Diagnostics;
using ClubOS.Shared;

namespace ClubOS.Service;

/// <summary>Выключение и перезагрузка компьютера. Отдельный интерфейс, чтобы тесты не перезагружали настоящий ПК.</summary>
public interface ISystemPower
{
    /// <summary>Запланировать команду через delaySeconds секунд. Возвращает, получилось ли, и текст для журнала администратора.</summary>
    Task<(bool Ok, string Message)> ScheduleAsync(string kind, int delaySeconds, bool force, CancellationToken ct);
}

/// <summary>Через shutdown.exe: у него есть задержка, а Windows сама показывает пользователю предупреждение. Служба работает от системы, прав хватает.</summary>
public sealed class WindowsSystemPower : ISystemPower
{
    public async Task<(bool Ok, string Message)> ScheduleAsync(string kind, int delaySeconds, bool force, CancellationToken ct)
    {
        var flag = kind switch { CommandRunner.Reboot => "/r", CommandRunner.Shutdown => "/s", _ => null };
        if (flag is null) return (false, "Неизвестная команда");
        var comment = kind == CommandRunner.Reboot ? "ClubOS: перезагрузка по команде администратора" : "ClubOS: выключение по команде администратора";
        var psi = new ProcessStartInfo("shutdown.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in new[] { flag, "/t", delaySeconds.ToString(), "/c", comment }) psi.ArgumentList.Add(a);
        if (force) psi.ArgumentList.Add("/f");
        try
        {
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("shutdown.exe не запустился");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (p.ExitCode == 0) return (true, "");
            var err = (await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false)).Trim();
            return (false, err.Length > 0 ? err : $"shutdown.exe вернул код {p.ExitCode}");
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return (false, $"Не удалось выполнить: {e.Message}");
        }
    }
}

/// <summary>Какие команды уже выполнены. Нужен, чтобы после перезагрузки сервер, не получивший подтверждения, не заставил ПК перезагрузиться снова.</summary>
public interface ICommandJournal
{
    bool Contains(int commandId);
    void Add(int commandId);
    void Forget(int commandId);   // выполнить не удалось: пусть команду можно будет повторить
}

public sealed class MemoryCommandJournal : ICommandJournal
{
    private readonly HashSet<int> _ids = [];
    public bool Contains(int commandId) => _ids.Contains(commandId);
    public void Add(int commandId) => _ids.Add(commandId);
    public void Forget(int commandId) => _ids.Remove(commandId);
}

/// <summary>
/// Пробный режим (CLUBOS_POWER_DRYRUN=1, для разработки и проверки): команда принимается и подтверждается, но ПК не перезагружается и не выключается.
/// В админке это видно по тексту результата.
/// </summary>
public sealed class DryRunSystemPower : ISystemPower
{
    public static bool Enabled => Environment.GetEnvironmentVariable("CLUBOS_POWER_DRYRUN") == "1";

    public Task<(bool Ok, string Message)> ScheduleAsync(string kind, int delaySeconds, bool force, CancellationToken ct) =>
        Task.FromResult((kind is CommandRunner.Reboot or CommandRunner.Shutdown, "(пробный режим: ПК не тронут)"));
}

/// <summary>
/// Файл «номер команды|время». Записям верим недолго: команды живут на сервере считанные минуты, а номера могут повториться, если сервер пересоздали.
/// Пишется по возможности: сбой диска не мешает работе.
/// </summary>
public sealed class FileCommandJournal(string path, TimeProvider? time = null, Action<string>? warn = null) : ICommandJournal
{
    public static readonly TimeSpan Remember = TimeSpan.FromMinutes(30);
    private const int Keep = 200;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();

    private List<(int Id, long At)> Read()
    {
        var list = new List<(int, long)>();
        try
        {
            if (!File.Exists(path)) return list;
            foreach (var line in File.ReadAllLines(path))
            {
                var parts = line.Split('|');
                if (parts.Length == 2 && int.TryParse(parts[0], out var id) && long.TryParse(parts[1], out var at)) list.Add((id, at));
            }
        }
        catch (Exception e) { warn?.Invoke($"Не удалось прочитать журнал команд: {e.GetType().Name}"); }
        return list;
    }

    public bool Contains(int commandId)
    {
        var from = (_time.GetUtcNow() - Remember).ToUnixTimeSeconds();
        lock (_gate) return Read().Any(e => e.Id == commandId && e.At >= from);
    }

    public void Forget(int commandId)
    {
        lock (_gate)
        {
            try
            {
                var rows = Read().Where(e => e.Id != commandId).ToList();
                if (File.Exists(path)) File.WriteAllLines(path, rows.Select(e => $"{e.Id}|{e.At}"));
            }
            catch (Exception e) { warn?.Invoke($"Не удалось обновить журнал команд: {e.GetType().Name}"); }
        }
    }

    public void Add(int commandId)
    {
        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        lock (_gate)
        {
            try
            {
                var rows = Read().Where(e => e.Id != commandId && e.At >= now - (long)Remember.TotalSeconds).TakeLast(Keep - 1).ToList();
                rows.Add((commandId, now));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllLines(path, rows.Select(e => $"{e.Id}|{e.At}"));
            }
            catch (Exception e) { warn?.Invoke($"Не удалось записать журнал команд: {e.GetType().Name}"); }
        }
    }
}

/// <summary>
/// Выполняет команды администратора для этого ПК. Правила:
///  • только известные команды (перезагрузка, выключение), ничего «произвольного» служба не запускает;
///  • идёт игровая сессия гостя, а администратор не подтвердил «всё равно»: команда не выполняется, администратору приходит причина;
///  • команда записывается в журнал до выполнения: если подтверждение не дошло и сервер повторит команду после перезагрузки, она не выполнится второй раз,
///    а будет только подтверждена;
///  • результат (выполнено или нет и почему) отправляется на сервер.
/// </summary>
public sealed class CommandRunner(ServerApi api, ISystemPower power, ICommandJournal journal, Action<string>? log = null, TimeSpan? retryPause = null)
{
    public const string Reboot = "reboot";
    public const string Shutdown = "shutdown";
    public const int DelaySeconds = 20;   // успеть закрыть сообщение и дать Windows предупредить пользователя

    public async Task HandleAsync(IReadOnlyList<CommandInfo> commands, AgentSnapshot snapshot, CancellationToken ct)
    {
        foreach (var c in commands.OrderBy(c => c.Id))
        {
            ct.ThrowIfCancellationRequested();
            if (journal.Contains(c.Id))                                   // уже выполняли (до перезагрузки): только подтверждаем
            {
                await ReportAsync(c.Id, true, "Выполнено (подтверждено после перезапуска службы)", ct).ConfigureAwait(false);
                continue;
            }
            if (c.Kind is not (Reboot or Shutdown))
            {
                await ReportAsync(c.Id, false, "Неизвестная команда, служба её не выполняет", ct).ConfigureAwait(false);
                continue;
            }
            var force = c.Payload?.Force == true;
            if (!force && snapshot.Mode == PcMode.GuestSession && snapshot.Session is not null)
            {
                await ReportAsync(c.Id, false, "Идёт сессия гостя: команда не выполнена", ct).ConfigureAwait(false);
                continue;
            }
            journal.Add(c.Id);                                           // до выполнения, см. выше
            var (ok, message) = await power.ScheduleAsync(c.Kind, DelaySeconds, force, ct).ConfigureAwait(false);
            if (!ok) journal.Forget(c.Id);
            var text = ok ? (c.Kind == Reboot ? $"Перезагрузка через {DelaySeconds} секунд" : $"Выключение через {DelaySeconds} секунд") + (message.Length > 0 ? " " + message : "") : message;
            log?.Invoke($"Команда {c.Id} ({c.Kind}): {text}");
            await ReportAsync(c.Id, ok, text, ct).ConfigureAwait(false);
        }
    }

    private async Task ReportAsync(int id, bool ok, string message, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var r = await api.PostAsync<System.Text.Json.JsonElement>($"/api/agent/commands/{id}/result", new { ok, message }, null, ct).ConfigureAwait(false);
            if (r.Ok || (r.Status is >= 400 and < 500)) return;          // принято или сервер отверг окончательно (команда уже закрыта, не наша)
            try { await Task.Delay((retryPause ?? TimeSpan.FromSeconds(1)) * (1 + attempt), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }
}
