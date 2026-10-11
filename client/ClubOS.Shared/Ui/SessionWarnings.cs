namespace ClubOS.Shared.Ui;

/// <summary>Короткое предупреждение гостю во время игры: заголовок и одна-две строки текста.</summary>
public sealed record SessionWarning(int Minutes, string Title, string Text);

/// <summary>
/// Решает, когда предупредить гостя, что время кончается: за 15, 10 и 5 минут. Каждая отметка срабатывает один раз.
/// Если времени стало больше (гость пополнил баланс), отметки взводятся снова. Если сессия началась с малым запасом
/// (например, на баланс хватает на 7 минут), приходит одно предупреждение, а не сразу три.
/// Не знает про окна и звук: тест проверяет только «когда» и «что написать».
/// </summary>
public sealed class SessionWarnings
{
    private static readonly int[] Marks = [15, 10, 5];
    private readonly HashSet<int> _fired = [];
    private (int SessionId, bool Overtime)? _phase;

    /// <summary>Вызывается на каждое новое состояние (раз в секунду). Возвращает предупреждение, если пора, иначе null.</summary>
    public SessionWarning? Check(AgentSnapshot s)
    {
        if (s.Mode != PcMode.GuestSession || s.Session is null || s.SecondsLeft is not int left)
        {
            _fired.Clear();
            _phase = null;
            return null;
        }

        // новая сессия или тариф кончился и пошла доплата по часам: счёт времени начинается заново
        var phase = (s.Session.Id, s.Session.Overtime);
        if (_phase != phase)
        {
            _fired.Clear();
            _phase = phase;
        }

        foreach (var mark in Marks)
            if (left > mark * 60) _fired.Remove(mark);   // времени снова много: предупредим ещё раз, когда оно подойдёт к концу

        var due = Marks.Where(m => left <= m * 60 && !_fired.Contains(m)).ToList();
        if (left <= 0 || due.Count == 0) return null;
        foreach (var mark in due) _fired.Add(mark);

        var minutes = (left + 59) / 60;
        var (title, text) = Describe(s, minutes);
        return new SessionWarning(minutes, title, text);
    }

    private static (string Title, string Text) Describe(AgentSnapshot s, int minutes)
    {
        var session = s.Session!;
        var m = $"{minutes} {Plural(minutes, "минуту", "минуты", "минут")}";
        // время кончается вместе с балансом: почасовой тариф или доплата после тарифа
        if (session.Overtime || session.Kind == "hourly")
            return ("Баланс кончается", $"Денег на балансе хватит ещё на {m}. Чтобы играть дальше, пополните баланс у администратора.");
        if (OvertimeCovered(s) is int rate)
            return ("Тариф заканчивается", $"Через {m} тариф закончится. Дальше почасовая оплата {rate} ₽/ч с баланса.");
        return ("Сессия заканчивается", $"Через {m} сессия закончится. Чтобы играть дальше, пополните баланс у администратора.");
    }

    /// <summary>Ставка доплаты, если после конца тарифа на балансе хватит хотя бы на минуту игры; иначе null.</summary>
    private static int? OvertimeCovered(AgentSnapshot s) =>
        s.Session?.OvertimePrice is int rate && rate > 0 && (s.Account?.Balance ?? 0) * 60 >= rate ? rate : null;

    /// <summary>Пояснение под таймером в меню: что идёт сейчас и что будет после тарифа. Пусто, если пояснять нечего.</summary>
    public static string Note(AgentSnapshot s)
    {
        var session = s.Session;
        if (session is null) return "";
        if (session.Overtime)
            return $"Идёт почасовая оплата, {session.OvertimePrice} ₽/ч. Чтобы играть дольше, пополните баланс";
        if (session.Kind != "hourly" && session.OvertimePrice is int rate)
            return OvertimeCovered(s) is not null
                ? $"После тарифа почасовая оплата {rate} ₽/ч с баланса"
                : "После тарифа сессия закончится: баланса на доплату нет";
        return "";
    }

    /// <summary>1 минуту, 2 минуты, 5 минут (и 21 минуту, 11 минут).</summary>
    public static string Plural(int n, string one, string few, string many)
    {
        var mod100 = n % 100;
        if (mod100 is >= 11 and <= 14) return many;
        return (n % 10) switch { 1 => one, 2 or 3 or 4 => few, _ => many };
    }
}
