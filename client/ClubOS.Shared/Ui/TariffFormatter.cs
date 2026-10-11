using System.Globalization;

namespace ClubOS.Shared.Ui;

/// <summary>Тариф, готовый к показу в кабинете: описание, можно ли начать с текущим балансом и подсказка, если нельзя.</summary>
public sealed record TariffView(int Id, string Name, string PriceText, string Description, bool CanStart, string? Hint);

public static class TariffFormatter
{
    /// <summary>Сколько минут почасового тарифа должно хватать на балансе для старта (как на сервере, MIN_HOURLY_MINUTES).</summary>
    public const int MinHourlyMinutes = 10;

    public static int MinBalance(TariffInfo t) =>
        t.Kind == "hourly" ? (int)Math.Ceiling(t.Price * MinHourlyMinutes / 60.0) : t.Price;

    /// <summary>Крупная цена на карточке тарифа: «120 ₽/ч» для почасового, «250 ₽» для остальных.</summary>
    public static string PriceText(TariffInfo t) => t.Kind == "hourly" ? $"{t.Price} ₽/ч" : $"{t.Price} ₽";

    /// <summary>Условия под названием тарифа (цена вынесена отдельно, см. PriceText).</summary>
    public static string Describe(TariffInfo t, int? balance = null) => t.Kind switch
    {
        "hourly" when balance is int b && b >= MinBalance(t)
            => $"Списывается по факту. Баланса хватит на {FormatMinutes(b * 60 / Math.Max(t.Price, 1))}",
        "hourly" => "Списывается по факту",
        "package" => $"На {FormatMinutes(t.PackageMinutes ?? 0)}",
        "night" => $"С {t.NightStart:00}:00 до {t.NightEnd:00}:00",
        _ => "",
    };

    public static TariffView ToView(TariffInfo t, int balance)
    {
        var need = MinBalance(t);
        var can = balance >= need;
        return new(t.Id, t.Name, PriceText(t), Describe(t, balance), can, can ? null : $"Не хватает {need - balance} ₽");
    }

    /// <summary>
    /// Карточка продления: купить ещё один пакет, пока идёт сессия. Время добавляется к оставшемуся, цена списывается с баланса сразу.
    /// Если тариф уже кончился и шла доплата по часам, сервер сначала закроет её, поэтому ему может не хватить чуть больше, чем показано.
    /// </summary>
    public static TariffView ToExtendView(TariffInfo t, int balance)
    {
        var can = balance >= t.Price;
        return new(t.Id, t.Name, $"{t.Price} ₽", $"Добавить {FormatMinutes(t.PackageMinutes ?? 0)} к вашему времени", can, can ? null : $"Не хватает {t.Price - balance} ₽");
    }

    /// <summary>180 → «3 ч», 90 → «1 ч 30 мин», 45 → «45 мин».</summary>
    public static string FormatMinutes(int minutes)
    {
        var h = minutes / 60;
        var m = minutes % 60;
        return (h, m) switch
        {
            (0, _) => $"{m} мин",
            (_, 0) => $"{h} ч",
            _ => $"{h} ч {m} мин",
        };
    }

    /// <summary>Оставшееся время для таймера: 3725 → «1:02:05», 125 → «02:05».</summary>
    public static string FormatClock(int seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(seconds, 0));
        return t.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{t.Minutes:00}:{t.Seconds:00}");
    }
}
