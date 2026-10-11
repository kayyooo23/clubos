namespace ClubOS.Shared.Ui;

/// <summary>Проверки на экране до отправки: чтобы гость сразу видел понятную подсказку. Окончательно всё равно проверяет сервер.</summary>
public static class Validation
{
    public const int MinPassword = 8;

    public static string? Phone(string? phone)
    {
        var digits = (phone ?? "").Count(char.IsDigit);
        return digits is >= 10 and <= 15 ? null : "Введите номер телефона полностью, например +7 900 123-45-67";
    }

    public static string? Code(string? code) =>
        code is { Length: 6 } && code.All(char.IsDigit) ? null : "Код из SMS состоит из 6 цифр";

    public static string? NewPassword(string? password, string? repeat)
    {
        if ((password ?? "").Length < MinPassword) return $"Пароль: минимум {MinPassword} символов";
        if (password != repeat) return "Пароли не совпадают";
        return null;
    }

    public static string? LoginPassword(string? password) =>
        string.IsNullOrEmpty(password) ? "Введите пароль" : null;
}
