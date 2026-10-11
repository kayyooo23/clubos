using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ClubOS.Overlay;

/// <summary>Показывает элемент, только если значение (например, текущий экран) совпадает с параметром: Visibility="{Binding Screen, Converter=..., ConverterParameter=Login}".</summary>
public sealed class EqualsToVisibility : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Обратное к EqualsToVisibility: элемент виден, пока значение НЕ совпадает с параметром.</summary>
public sealed class NotEqualsToVisibility : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Наоборот: элемент виден, пока текст ПУСТОЙ (подсказка внутри поля поиска).</summary>
public sealed class NoTextToVisibility : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Наоборот к BooleanToVisibilityConverter: элемент виден, пока значение false.</summary>
public sealed class InvertBoolToVisibility : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Пустой текст → элемент скрыт.</summary>
public sealed class TextToVisibility : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Число секунд до повторной отправки кода → текст кнопки.</summary>
public sealed class ResendText : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int s && s > 0 ? $"Отправить код ещё раз через {s} с" : "Отправить код ещё раз";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class ZeroToTrue : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is int s && s == 0;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class Money : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int v ? $"{v:N0} ₽".Replace(' ', ' ') : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
