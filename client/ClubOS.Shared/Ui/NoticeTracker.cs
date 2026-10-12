namespace ClubOS.Shared.Ui;

/// <summary>
/// Решает, какие сообщения администратора показать гостю прямо сейчас. Сервер повторяет свежее сообщение в каждом ответе пару минут
/// (чтобы оно не потерялось при сбое связи), поэтому каждое сообщение запоминается по номеру и показывается один раз.
/// Не знает про окна и звук: тест проверяет только «что показать».
/// </summary>
public sealed class NoticeTracker
{
    private const int Remember = 100;
    private readonly HashSet<int> _shown = [];
    private readonly Queue<int> _order = new();

    /// <summary>Вызывается на каждое новое состояние. Возвращает ещё не показанные сообщения (старые первыми), без связи и на рабочем столе сотрудника ничего.</summary>
    public IReadOnlyList<NoticeInfo> Fresh(AgentSnapshot s)
    {
        if (!s.Connected || s.Mode == PcMode.StaffDesktop || s.Notices is not { Count: > 0 } all) return [];
        var fresh = new List<NoticeInfo>();
        foreach (var notice in all)
        {
            if (string.IsNullOrWhiteSpace(notice.Text) || !_shown.Add(notice.Id)) continue;
            _order.Enqueue(notice.Id);
            fresh.Add(notice);
        }
        while (_order.Count > Remember) _shown.Remove(_order.Dequeue());
        return fresh;
    }

    /// <summary>Текст для окна: несколько новых сообщений подряд склеиваются через пустую строку (окно одно).</summary>
    public static string Combine(IReadOnlyList<NoticeInfo> notices) => string.Join("\n\n", notices.Select(n => n.Text.Trim()));
}
