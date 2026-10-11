using System.Text;

namespace ClubOS.Shared.Ui;

/// <summary>
/// Маска российского номера для поля телефона: «+7 (900) 123-45-67». Гость вводит только цифры, остальное (+7, скобки, пробелы, дефис) расставляется само.
/// Ведущие «7», «8» и «+7» принимаются как код страны и в номер не попадают.
/// </summary>
public static class PhoneMask
{
    public const string Prefix = "+7 (";
    public const string Hint = "+7 (000) 000-00-00";
    public const int Digits = 10;

    /// <summary>Десять цифр номера без кода страны из любого текста: ввод руками, вставка «8 900 123-45-67» или уже отформатированная строка.</summary>
    public static string Extract(string? text)
    {
        text ??= "";
        var digits = new string(text.Where(char.IsDigit).ToArray());
        if (text.TrimStart().StartsWith("+7", StringComparison.Ordinal))
            digits = digits.Length > 0 ? digits[1..] : digits;               // «7» из «+7» не часть номера
        else if (digits.Length >= 11 && digits[0] is '7' or '8')
            digits = digits[1..];                                            // 8 900... и 7 900...
        return digits.Length > Digits ? digits[..Digits] : digits;
    }

    /// <summary>Цифры в маску. Пусто, пока цифр нет: тогда в поле видна подсказка.</summary>
    public static string Format(string digits)
    {
        if (digits.Length == 0) return "";
        var sb = new StringBuilder(Prefix);
        sb.Append(digits, 0, Math.Min(3, digits.Length));
        if (digits.Length > 3) sb.Append(") ").Append(digits, 3, Math.Min(3, digits.Length - 3));
        if (digits.Length > 6) sb.Append('-').Append(digits, 6, Math.Min(2, digits.Length - 6));
        if (digits.Length > 8) sb.Append('-').Append(digits, 8, digits.Length - 8);
        return sb.ToString();
    }

    public static string Apply(string? text) => Format(Extract(text));
}
