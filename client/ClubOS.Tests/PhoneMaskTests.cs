using ClubOS.Shared.Ui;

namespace ClubOS.Tests;

/// <summary>Маска телефона: гость вводит цифры, остальное расставляется само.</summary>
public class PhoneMaskTests
{
    [Theory]
    [InlineData("9001234567", "+7 (900) 123-45-67")]
    [InlineData("89001234567", "+7 (900) 123-45-67")]
    [InlineData("79001234567", "+7 (900) 123-45-67")]
    [InlineData("+7 900 123-45-67", "+7 (900) 123-45-67")]
    [InlineData("+7 (900) 123-45-67", "+7 (900) 123-45-67")]
    [InlineData("8 (900) 123-45-67", "+7 (900) 123-45-67")]
    [InlineData("900123456789999", "+7 (900) 123-45-67")]            // лишние цифры отбрасываются
    public void A_full_number_in_any_form_becomes_the_same_mask(string typed, string expected) =>
        Assert.Equal(expected, PhoneMask.Apply(typed));

    [Theory]
    [InlineData("", "")]
    [InlineData("9", "+7 (9")]
    [InlineData("90", "+7 (90")]
    [InlineData("900", "+7 (900")]
    [InlineData("9001", "+7 (900) 1")]
    [InlineData("900123", "+7 (900) 123")]
    [InlineData("9001234", "+7 (900) 123-4")]
    [InlineData("900123456", "+7 (900) 123-45-6")]
    public void The_mask_grows_with_every_digit(string digits, string expected) =>
        Assert.Equal(expected, PhoneMask.Format(digits));

    [Theory]
    [InlineData("abc9d0e0", "900")]
    [InlineData("+7 (", "")]
    [InlineData("+7 (9", "9")]
    [InlineData("+7 (900) 1", "9001")]
    public void Letters_and_signs_are_ignored_and_the_country_code_is_not_part_of_the_number(string text, string digits) =>
        Assert.Equal(digits, PhoneMask.Extract(text));

    [Fact]
    public void The_masked_number_passes_the_same_validation_as_before() =>
        Assert.Null(Validation.Phone(PhoneMask.Apply("9001234567")));

    [Fact]
    public void An_unfinished_number_does_not_pass_validation() =>
        Assert.NotNull(Validation.Phone(PhoneMask.Apply("90012")));
}
