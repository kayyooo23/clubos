using ClubOS.Shared;
using ClubOS.Shared.Ui;

namespace ClubOS.Tests;

public class FormattingTests
{
    [Theory]
    [InlineData(180, "3 ч")]
    [InlineData(90, "1 ч 30 мин")]
    [InlineData(45, "45 мин")]
    [InlineData(0, "0 мин")]
    [InlineData(60, "1 ч")]
    public void Minutes(int minutes, string expected) => Assert.Equal(expected, TariffFormatter.FormatMinutes(minutes));

    [Theory]
    [InlineData(3725, "1:02:05")]
    [InlineData(125, "02:05")]
    [InlineData(59, "00:59")]
    [InlineData(0, "00:00")]
    [InlineData(-5, "00:00")]
    [InlineData(36000, "10:00:00")]
    public void Clock(int seconds, string expected) => Assert.Equal(expected, TariffFormatter.FormatClock(seconds));

    [Fact]
    public void Tariff_descriptions_in_plain_russian()
    {
        Assert.Equal("На 3 ч", TariffFormatter.Describe(new(2, "3 часа", "package", 250, 180, null, null)));
        Assert.Equal("С 23:00 до 08:00", TariffFormatter.Describe(new(3, "Ночь", "night", 400, null, 23, 8)));
        Assert.Equal("Списывается по факту", TariffFormatter.Describe(new(1, "Час", "hourly", 120, null, null, null)));
        Assert.Equal("120 ₽/ч", TariffFormatter.PriceText(new(1, "Час", "hourly", 120, null, null, null)));
        Assert.Equal("250 ₽", TariffFormatter.PriceText(new(2, "3 часа", "package", 250, 180, null, null)));
        Assert.Equal("400 ₽", TariffFormatter.PriceText(new(3, "Ночь", "night", 400, null, 23, 8)));
    }

    [Fact]
    public void Hourly_description_tells_how_long_the_balance_lasts()
    {
        var hourly = new TariffInfo(1, "Час", "hourly", 120, null, null, null);
        Assert.Contains("Баланса хватит на 50 мин", TariffFormatter.Describe(hourly, 100));
        Assert.Contains("1 ч 30 мин", TariffFormatter.Describe(hourly, 180));
        Assert.DoesNotContain("хватит", TariffFormatter.Describe(hourly, 10));   // меньше минимума
    }

    [Fact]
    public void Minimum_balance_matches_the_server_rule()
    {
        // сервер: hourly — ceil(price * 10 / 60), остальные — вся цена
        Assert.Equal(20, TariffFormatter.MinBalance(new(1, "", "hourly", 120, null, null, null)));
        Assert.Equal(1, TariffFormatter.MinBalance(new(1, "", "hourly", 1, null, null, null)));
        Assert.Equal(250, TariffFormatter.MinBalance(new(2, "", "package", 250, 180, null, null)));
    }

    [Fact]
    public void Tariff_view_says_how_much_is_missing()
    {
        var pkg = new TariffInfo(2, "3 часа", "package", 250, 180, null, null);
        var no = TariffFormatter.ToView(pkg, 200);
        Assert.False(no.CanStart);
        Assert.Equal("Не хватает 50 ₽", no.Hint);
        var yes = TariffFormatter.ToView(pkg, 250);
        Assert.True(yes.CanStart);
        Assert.Null(yes.Hint);
    }

    [Theory]
    [InlineData("+7 900 123-45-67", true)]
    [InlineData("8 (900) 123-45-67", true)]
    [InlineData("89001234567", true)]
    [InlineData("12345", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("abc", false)]
    public void Phone_check(string? phone, bool ok) => Assert.Equal(ok, Validation.Phone(phone) is null);

    [Theory]
    [InlineData("123456", true)]
    [InlineData("12345", false)]
    [InlineData("1234567", false)]
    [InlineData("12345a", false)]
    [InlineData("", false)]
    public void Code_check(string code, bool ok) => Assert.Equal(ok, Validation.Code(code) is null);

    [Fact]
    public void Password_check()
    {
        Assert.Null(Validation.NewPassword("12345678", "12345678"));
        Assert.NotNull(Validation.NewPassword("1234567", "1234567"));
        Assert.NotNull(Validation.NewPassword("12345678", "12345679"));
        Assert.NotNull(Validation.NewPassword(null, null));
    }

    [Fact]
    public void Operation_whitelist_has_no_surprises()
    {
        Assert.Equal(22, Ops.All.Count);   // 12 прежних + apps + бар (товары, заказ, заказы, отмена) + вызов администратора (2) + реклама (список и «заставка на экране»)
        Assert.All(Ops.All, op => Assert.DoesNotContain("/", op));
    }
}
