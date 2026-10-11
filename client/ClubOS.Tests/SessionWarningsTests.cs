using ClubOS.Shared;
using ClubOS.Shared.Ui;

namespace ClubOS.Tests;

public class SessionWarningsTests
{
    private static AgentSnapshot Playing(int left, int balance = 250, string kind = "package", bool overtime = false,
        int? rate = 120, int sessionId = 1, PcMode mode = PcMode.GuestSession) =>
        new(true, mode, new AccountInfo(2, "guest", "+7 *** ***-00-01", balance),
            new SessionInfo(sessionId, 1, 2, "3 часа", DateTime.UtcNow, null, null, 250, left, kind, overtime, rate), null, left);

    private static int Minutes(SessionWarning? w) => w?.Minutes ?? -1;

    // ---------- когда предупреждать ----------
    [Fact]
    public void Nothing_while_there_is_plenty_of_time()
    {
        var w = new SessionWarnings();
        Assert.Null(w.Check(Playing(3 * 3600)));
        Assert.Null(w.Check(Playing(16 * 60)));
    }

    [Fact]
    public void Warns_at_15_10_and_5_minutes_once_each()
    {
        var w = new SessionWarnings();
        w.Check(Playing(20 * 60));
        Assert.Equal(15, Minutes(w.Check(Playing(15 * 60))));
        Assert.Null(w.Check(Playing(15 * 60 - 1)));           // та же отметка второй раз не срабатывает
        Assert.Null(w.Check(Playing(11 * 60)));
        Assert.Equal(10, Minutes(w.Check(Playing(10 * 60))));
        Assert.Null(w.Check(Playing(9 * 60)));
        Assert.Equal(5, Minutes(w.Check(Playing(5 * 60))));
        Assert.Null(w.Check(Playing(4 * 60)));
        Assert.Null(w.Check(Playing(60)));
        Assert.Null(w.Check(Playing(0)));
    }

    [Fact]
    public void A_late_start_gives_one_warning_not_three()
    {
        var w = new SessionWarnings();
        var first = w.Check(Playing(7 * 60, kind: "hourly", rate: null));   // на баланс хватает на 7 минут
        Assert.Equal(7, Minutes(first));
        Assert.Null(w.Check(Playing(7 * 60 - 1, kind: "hourly", rate: null)));
        Assert.Equal(5, Minutes(w.Check(Playing(5 * 60, kind: "hourly", rate: null))));
    }

    [Fact]
    public void Topping_up_rearms_the_warnings()
    {
        var w = new SessionWarnings();
        Assert.Equal(5, Minutes(w.Check(Playing(5 * 60))));
        Assert.Null(w.Check(Playing(25 * 60)));                // гость пополнил баланс, времени стало больше
        Assert.Equal(15, Minutes(w.Check(Playing(15 * 60))));  // и предупреждение придёт снова
    }

    [Fact]
    public void Overtime_starts_a_fresh_countdown_for_the_balance()
    {
        var w = new SessionWarnings();
        Assert.Equal(5, Minutes(w.Check(Playing(5 * 60))));
        Assert.Null(w.Check(Playing(2 * 3600, overtime: true)));   // тариф кончился, на балансе ещё 2 часа
        Assert.Equal(15, Minutes(w.Check(Playing(15 * 60, overtime: true))));
    }

    [Fact]
    public void A_new_session_forgets_the_old_one()
    {
        var w = new SessionWarnings();
        Assert.Equal(5, Minutes(w.Check(Playing(5 * 60, sessionId: 1))));
        Assert.Null(w.Check(Playing(3 * 3600, sessionId: 2)));
        Assert.Equal(5, Minutes(w.Check(Playing(5 * 60, sessionId: 2))));
    }

    [Fact]
    public void No_session_or_a_locked_pc_means_no_warning_and_a_clean_slate()
    {
        var w = new SessionWarnings();
        Assert.Equal(5, Minutes(w.Check(Playing(5 * 60))));
        Assert.Null(w.Check(new AgentSnapshot(true, PcMode.Locked, null, null, null, null)));
        Assert.Equal(5, Minutes(w.Check(Playing(5 * 60))));    // новая сессия с тем же номером после блокировки: снова предупредим
        Assert.Null(w.Check(Playing(300, mode: PcMode.StaffDesktop)));
        // без таймера (почасовой без лимита) предупреждать не о чем
        Assert.Null(w.Check(new AgentSnapshot(true, PcMode.GuestSession, null,
            new SessionInfo(9, 1, 2, "Час", DateTime.UtcNow, null, null, 0, null, "hourly"), null, null)));
    }

    // ---------- что написать ----------
    [Fact]
    public void Package_with_money_for_overtime_says_hourly_payment_follows()
    {
        var w = new SessionWarnings();
        var warning = w.Check(Playing(5 * 60, balance: 250, rate: 120))!;
        Assert.Equal("Тариф заканчивается", warning.Title);
        Assert.Contains("Через 5 минут тариф закончится", warning.Text);
        Assert.Contains("120 ₽/ч", warning.Text);
    }

    [Fact]
    public void Package_without_money_for_overtime_asks_to_top_up_before_it_ends()
    {
        var w = new SessionWarnings();
        var warning = w.Check(Playing(10 * 60, balance: 0, rate: 120))!;
        Assert.Equal("Сессия заканчивается", warning.Title);
        Assert.Contains("Через 10 минут сессия закончится", warning.Text);
        Assert.Contains("пополните баланс", warning.Text);
    }

    [Fact]
    public void Package_in_a_club_without_hourly_tariff_also_asks_to_top_up_only_to_extend()
    {
        var w = new SessionWarnings();
        var warning = w.Check(Playing(5 * 60, balance: 500, rate: null))!;   // доплаты нет в клубе вообще
        Assert.Equal("Сессия заканчивается", warning.Title);
    }

    [Fact]
    public void Hourly_and_overtime_warn_about_the_balance_running_out()
    {
        var hourly = new SessionWarnings().Check(Playing(5 * 60, kind: "hourly", rate: null))!;
        Assert.Equal("Баланс кончается", hourly.Title);
        Assert.Contains("хватит ещё на 5 минут", hourly.Text);

        var overtime = new SessionWarnings().Check(Playing(5 * 60, overtime: true))!;
        Assert.Equal("Баланс кончается", overtime.Title);
    }

    [Theory]
    [InlineData(1, "минуту")]
    [InlineData(2, "минуты")]
    [InlineData(4, "минуты")]
    [InlineData(5, "минут")]
    [InlineData(11, "минут")]
    [InlineData(12, "минут")]
    [InlineData(14, "минут")]
    [InlineData(15, "минут")]
    [InlineData(21, "минуту")]
    [InlineData(22, "минуты")]
    public void Minutes_are_declined_in_russian(int n, string expected) =>
        Assert.Equal(expected, SessionWarnings.Plural(n, "минуту", "минуты", "минут"));

    [Fact]
    public void The_menu_note_explains_what_happens_after_the_tariff()
    {
        Assert.Equal("После тарифа почасовая оплата 120 ₽/ч с баланса", SessionWarnings.Note(Playing(3600, balance: 250, rate: 120)));
        Assert.Equal("После тарифа сессия закончится: баланса на доплату нет", SessionWarnings.Note(Playing(3600, balance: 0, rate: 120)));
        Assert.StartsWith("Идёт почасовая оплата, 120 ₽/ч", SessionWarnings.Note(Playing(3600, overtime: true)));
        Assert.Equal("", SessionWarnings.Note(Playing(3600, kind: "hourly", rate: null)));
        Assert.Equal("", SessionWarnings.Note(Playing(3600, rate: null)));
        Assert.Equal("", SessionWarnings.Note(new AgentSnapshot(true, PcMode.Locked, null, null, null, null)));
    }
}
