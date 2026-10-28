using ClubOS.Shared;

namespace ClubOS.Service.Update;

/// <summary>
/// Подводит итог обновления после перезапуска службы. Помощник пишет в файл состояния, что переключает версию, а дальше работает та служба, которая
/// теперь запущена:
///  • это новая версия и она вышла на связь: пишет отметку «работаю» (её ждёт помощник), сообщает серверу «ok», убирает лишние старые версии;
///  • это прежняя версия и помощник записал откат: сообщает серверу, что новая не заработала (и почему);
///  • это прежняя версия, а переключение «зависло» (помощник умер, питание): через StaleAfter считает его неудавшимся.
/// Сообщение серверу повторяется, пока не дойдёт (Reported хранится в файле).
/// </summary>
public sealed class UpdateReconciler(ServerApi api, UpdateStateStore state, BadVersions bad, InstallLayout layout, string healthFile, string currentVersion,
    TimeProvider? time = null, Action<string>? log = null)
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Вызывается, когда есть связь с сервером. Дёшево: если обновления не было, читает один маленький файл.</summary>
    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        var s = state.Load();
        if (s is null || (s.Reported && s.State != "switching")) return;
        var now = _time.GetUtcNow();

        if (s.State == "switching")
        {
            if (s.To == currentVersion)
            {
                HealthFile.Write(healthFile, currentVersion, now);                       // помощник ждёт именно это
                s = s with { State = "ok", At = now.ToUnixTimeSeconds(), Message = null };
                state.Save(s);
                log?.Invoke($"Новая версия {currentVersion} вышла на связь");
            }
            else if (s.From == currentVersion && now.ToUnixTimeSeconds() - s.At > StaleAfter.TotalSeconds)
            {
                bad.Add(s.To);
                s = s with { State = "failed", At = now.ToUnixTimeSeconds(), Message = "Переключение версии не завершилось" };
                state.Save(s);
                log?.Invoke(s.Message!);
            }
            else return;                                                                // помощник ещё работает
        }

        if (s.State == "ok" && s.To == currentVersion)
        {
            if (await ReportAsync(s.To, "ok", null, ct).ConfigureAwait(false))
            {
                state.Save(s with { Reported = true });
                CleanOldVersions(keep: [currentVersion, s.From]);
            }
        }
        else if (s.State is "rolled_back" or "failed" && s.From == currentVersion)
        {
            if (await ReportAsync(s.To, s.State == "rolled_back" ? "rolled_back" : "failed", s.Message, ct).ConfigureAwait(false))
                state.Save(s with { Reported = true });
        }
    }

    private async Task<bool> ReportAsync(string version, string status, string? message, CancellationToken ct)
    {
        var r = await api.PostAsync<System.Text.Json.JsonElement>("/api/agent/update/result", new { version, status, message }, null, ct).ConfigureAwait(false);
        return r.Ok || r.Status is >= 400 and < 500;                                     // 4xx: сервер не примет и повтор, не зацикливаемся
    }

    /// <summary>Остаются работающая и предыдущая версии (на случай отката); недокачанные и недораспакованные папки уходят.</summary>
    private void CleanOldVersions(string[] keep)
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(layout.VersionsRoot))
            {
                var name = Path.GetFileName(dir);
                if (keep.Contains(name)) continue;
                try { Directory.Delete(dir, true); log?.Invoke($"Удалена старая версия {name}"); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* занята: удалится при следующем обновлении */ }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* папки нет или нет доступа: не страшно */ }
    }
}
