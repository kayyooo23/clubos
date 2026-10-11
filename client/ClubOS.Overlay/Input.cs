using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ClubOS.Shared.Ui;

namespace ClubOS.Overlay;

/// <summary>
/// Поведение полей ввода на экране входа (подключается свойствами в XAML):
///   Input.Mask="Phone"  только цифры, номер сам собирается в «+7 (900) 123-45-67», вставка «8 900 123-45-67» тоже разбирается;
///   Input.Mask="Digits" только цифры (код из SMS);
///   Input.Hint="..."    серая подсказка, пока поле пустое;
///   Input.Reveal        у поля пароля справа «глазик»: показать пароль открытым текстом (скрывается, когда поле пропадает с экрана).
/// </summary>
public static class Input
{
    public static readonly DependencyProperty HintProperty = DependencyProperty.RegisterAttached("Hint", typeof(string), typeof(Input), new PropertyMetadata(""));
    public static string GetHint(DependencyObject d) => (string)d.GetValue(HintProperty);
    public static void SetHint(DependencyObject d, string value) => d.SetValue(HintProperty, value);

    public static readonly DependencyProperty MaskProperty = DependencyProperty.RegisterAttached("Mask", typeof(string), typeof(Input), new PropertyMetadata(null, OnMaskChanged));
    public static string? GetMask(DependencyObject d) => (string?)d.GetValue(MaskProperty);
    public static void SetMask(DependencyObject d, string? value) => d.SetValue(MaskProperty, value);

    public static readonly DependencyProperty RevealProperty = DependencyProperty.RegisterAttached("Reveal", typeof(bool), typeof(Input), new PropertyMetadata(false, OnRevealChanged));
    public static bool GetReveal(DependencyObject d) => (bool)d.GetValue(RevealProperty);
    public static void SetReveal(DependencyObject d, bool value) => d.SetValue(RevealProperty, value);

    // ---------- маски ----------
    private static void OnMaskChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box || e.NewValue is not string mask) return;
        var phone = mask == "Phone";
        var previous = box.Text ?? "";
        var busy = false;

        box.PreviewTextInput += (_, args) =>
        {
            if (args.Text.Any(c => !char.IsDigit(c))) { args.Handled = true; return; }
            // первая «7» или «8» на пустом поле: гость набирает код страны, показываем «+7 (» и ждём остальное
            if (phone && args.Text is "7" or "8" && PhoneMask.Extract(box.Text).Length == 0 && box.SelectionLength == 0)
            {
                args.Handled = true;
                Set(PhoneMask.Prefix);
            }
        };
        box.PreviewKeyDown += (_, args) => { if (args.Key == Key.Space) args.Handled = true; };
        DataObject.AddPastingHandler(box, (_, args) =>
        {
            args.CancelCommand();
            var text = args.DataObject.GetData(DataFormats.UnicodeText) as string ?? "";
            Set(phone ? PhoneMask.Apply(text) : new string(text.Where(char.IsDigit).ToArray()));
        });
        box.TextChanged += (_, _) =>
        {
            if (busy) return;
            var text = box.Text ?? "";
            string result;
            if (phone)
            {
                var digits = PhoneMask.Extract(text);
                // «+7 (» без цифр оставляем, только пока гость его набрал; стирание до конца убирает и его (иначе Backspace упирался бы в скобку)
                result = digits.Length == 0
                    ? (text.Length >= previous.Length && text.StartsWith(PhoneMask.Prefix, StringComparison.Ordinal) ? PhoneMask.Prefix : "")
                    : PhoneMask.Format(digits);
            }
            else result = new string(text.Where(char.IsDigit).ToArray());
            if (result != text) Set(result); else previous = text;
        };

        void Set(string value)
        {
            busy = true;
            try { box.Text = value; box.CaretIndex = value.Length; previous = value; }
            finally { busy = false; }
        }
    }

    // ---------- глазик у пароля ----------
    private static void OnRevealChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PasswordBox box && e.NewValue is true) box.Loaded += (_, _) => Wire(box);
    }

    private static void Wire(PasswordBox box)
    {
        if (box.Template.FindName("PART_Eye", box) is not ToggleButton eye
            || box.Template.FindName("PART_Plain", box) is not TextBox plain
            || box.Template.FindName("PART_ContentHost", box) is not UIElement host
            || eye.Tag is "wired") return;
        eye.Tag = "wired";
        var syncing = false;

        eye.Checked += (_, _) =>
        {
            syncing = true;
            plain.Text = box.Password;
            syncing = false;
            host.Visibility = Visibility.Collapsed;
            plain.Visibility = Visibility.Visible;
            plain.Focus();
            plain.CaretIndex = plain.Text.Length;
        };
        eye.Unchecked += (_, _) =>
        {
            plain.Visibility = Visibility.Collapsed;
            host.Visibility = Visibility.Visible;
            syncing = true;                                  // очистка копии не должна стирать настоящий пароль
            plain.Text = "";                                 // открытая копия пароля нигде не хранится
            syncing = false;
            box.Focus();
        };
        plain.TextChanged += (_, _) => { if (!syncing) box.Password = plain.Text; };
        box.PasswordChanged += (_, _) =>
        {
            if (eye.IsChecked != true || plain.Text == box.Password) return;
            syncing = true;
            plain.Text = box.Password;
            syncing = false;
        };
        box.IsVisibleChanged += (_, args) => { if (args.NewValue is false) eye.IsChecked = false; };   // следующему гостю чужой пароль не показываем
    }

    /// <summary>Снова спрятать пароль (после входа, смены экрана, возврата заставки).</summary>
    public static void Conceal(PasswordBox box)
    {
        if (box.Template?.FindName("PART_Eye", box) is ToggleButton { IsChecked: true } eye) eye.IsChecked = false;
    }
}
