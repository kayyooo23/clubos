using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ClubOS.Overlay;

namespace ClubOS.Tests;

/// <summary>Поля входа: телефон по маске и только цифрами, подсказка в пустом поле, «глазик» у пароля. Проверяем настоящие поля с оформлением приложения.</summary>
public class InputTests
{
    private static void Pump(Window w) => w.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => { }));

    private static (Window Window, T Control) Show<T>(T control) where T : FrameworkElement
    {
        var window = new Window
        {
            Content = control, Width = 500, Height = 200, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
        };
        window.Show();
        Pump(window);
        return (window, control);
    }

    private static TextBox PhoneBox()
    {
        var box = new TextBox { Width = 400 };
        Input.SetMask(box, "Phone");
        Input.SetHint(box, "+7 (000) 000-00-00");
        return box;
    }

    private static bool TypeChar(UIElement target, string text)
    {
        var args = new TextCompositionEventArgs(Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, target, text)) { RoutedEvent = UIElement.PreviewTextInputEvent };
        target.RaiseEvent(args);
        return args.Handled;
    }

    private static T Part<T>(Control control, string name) where T : class => (T)control.Template.FindName(name, control);

    [Theory]
    [InlineData("9001234567", "+7 (900) 123-45-67")]
    [InlineData("89001234567", "+7 (900) 123-45-67")]
    [InlineData("+7 900 123", "+7 (900) 123")]
    [InlineData("abc", "")]
    public void The_phone_field_turns_whatever_arrives_into_the_mask(string typed, string expected)
    {
        WpfHost.Run(() =>
        {
            var (window, box) = Show(PhoneBox());
            box.Text = typed;
            Assert.Equal(expected, box.Text);
            window.Close();
        });
    }

    [Fact]
    public void Letters_and_spaces_cannot_be_typed_into_the_phone_field()
    {
        WpfHost.Run(() =>
        {
            var (window, box) = Show(PhoneBox());
            Assert.True(TypeChar(box, "a"));
            Assert.True(TypeChar(box, "я"));
            Assert.True(TypeChar(box, " "));
            Assert.True(TypeChar(box, "+"));
            Assert.False(TypeChar(box, "9"));                       // цифру поле принимает
            window.Close();
        });
    }

    [Fact]
    public void A_leading_8_or_7_is_taken_as_the_country_code()
    {
        WpfHost.Run(() =>
        {
            var (window, box) = Show(PhoneBox());
            Assert.True(TypeChar(box, "8"));
            Assert.Equal("+7 (", box.Text);                          // «8» не попал в номер, но гость видит, что поле приняло ввод
            box.Text = "+7 (9";
            Assert.Equal("+7 (9", box.Text);
            window.Close();
        });
    }

    [Fact]
    public void Erasing_the_last_digit_clears_the_prefix_so_backspace_never_gets_stuck()
    {
        WpfHost.Run(() =>
        {
            var (window, box) = Show(PhoneBox());
            box.Text = "+7 (900) 1";
            box.Text = "+7 (900) ";                                  // как после Backspace
            Assert.Equal("+7 (900", box.Text);
            box.Text = "+7 (9";
            box.Text = "+7 (";                                       // стёрта последняя цифра
            Assert.Equal("", box.Text);
            window.Close();
        });
    }

    [Fact]
    public void The_empty_phone_field_shows_the_hint_and_a_filled_one_does_not()
    {
        WpfHost.Run(() =>
        {
            var (window, box) = Show(PhoneBox());
            var hint = Part<TextBlock>(box, "HintText");
            Assert.Equal("+7 (000) 000-00-00", hint.Text);
            Assert.Equal(Visibility.Visible, hint.Visibility);
            box.Text = "9";
            Assert.Equal(Visibility.Collapsed, hint.Visibility);
            window.Close();
        });
    }

    [Fact]
    public void The_sms_code_field_takes_digits_only()
    {
        WpfHost.Run(() =>
        {
            var box = new TextBox { Width = 200, MaxLength = 6 };
            Input.SetMask(box, "Digits");
            var (window, _) = Show(box);
            Assert.True(TypeChar(box, "x"));
            Assert.False(TypeChar(box, "5"));
            box.Text = "12ab34";
            Assert.Equal("1234", box.Text);
            window.Close();
        });
    }

    [Fact]
    public void The_password_field_has_an_eye_that_shows_and_hides_the_password()
    {
        WpfHost.Run(() =>
        {
            var (window, box) = Show(new PasswordBox { Width = 400 });
            var eye = Part<ToggleButton>(box, "PART_Eye");
            var plain = Part<TextBox>(box, "PART_Plain");
            var host = Part<UIElement>(box, "PART_ContentHost");
            box.Password = "секрет-123";

            Assert.Equal(Visibility.Collapsed, plain.Visibility);          // по умолчанию пароль скрыт
            eye.IsChecked = true;
            Pump(window);
            Assert.Equal(Visibility.Visible, plain.Visibility);
            Assert.Equal(Visibility.Collapsed, host.Visibility);
            Assert.Equal("секрет-123", plain.Text);

            eye.IsChecked = false;
            Pump(window);
            Assert.Equal(Visibility.Collapsed, plain.Visibility);
            Assert.Equal(Visibility.Visible, host.Visibility);
            Assert.Equal("", plain.Text);                                  // открытая копия не остаётся
            Assert.Equal("секрет-123", box.Password);
            window.Close();
        });
    }

    [Fact]
    public void Typing_into_the_revealed_password_changes_the_real_password()
    {
        WpfHost.Run(() =>
        {
            var (window, box) = Show(new PasswordBox { Width = 400 });
            var changed = new List<string>();
            box.PasswordChanged += (_, _) => changed.Add(box.Password);
            var eye = Part<ToggleButton>(box, "PART_Eye");
            var plain = Part<TextBox>(box, "PART_Plain");
            eye.IsChecked = true;
            plain.Text = "abc12345";
            Assert.Equal("abc12345", box.Password);
            Assert.Contains("abc12345", changed);                          // модель входа узнаёт о пароле так же, как при обычном вводе
            window.Close();
        });
    }

    [Fact]
    public void A_cleared_password_is_hidden_again()
    {
        WpfHost.Run(() =>
        {
            var (window, box) = Show(new PasswordBox { Width = 400 });
            var eye = Part<ToggleButton>(box, "PART_Eye");
            eye.IsChecked = true;
            Input.Conceal(box);
            Assert.False(eye.IsChecked);
            window.Close();
        });
    }

    [Fact]
    public void A_password_field_that_leaves_the_screen_hides_the_password()
    {
        WpfHost.Run(() =>
        {
            var (window, box) = Show(new PasswordBox { Width = 400 });
            var eye = Part<ToggleButton>(box, "PART_Eye");
            eye.IsChecked = true;
            box.Visibility = Visibility.Collapsed;
            Pump(window);
            Assert.False(eye.IsChecked);                                   // следующему гостю чужой пароль не показываем
            window.Close();
        });
    }

    [Fact]
    public void All_login_fields_have_one_height_and_the_eye_does_not_change_the_size_of_the_password_field()
    {
        WpfHost.Run(() =>
        {
            var phone = PhoneBox();
            var code = new TextBox { Width = 400 };
            var password = new PasswordBox { Width = 400 };
            var panel = new StackPanel { Width = 420 };
            panel.Children.Add(phone);
            panel.Children.Add(code);
            panel.Children.Add(password);
            var (window, _) = Show(panel);
            var eye = Part<ToggleButton>(password, "PART_Eye");
            password.Password = "секрет-123";
            Pump(window);

            var hidden = password.ActualHeight;
            Assert.Equal(phone.ActualHeight, hidden, 1);                    // телефон и пароль одной высоты
            Assert.Equal(code.ActualHeight, hidden, 1);

            eye.IsChecked = true;
            Pump(window);
            Assert.Equal(hidden, password.ActualHeight, 1);                 // «показать» не меняет размер рамки
            Assert.Equal(400, password.ActualWidth, 1);

            eye.IsChecked = false;
            Pump(window);
            Assert.Equal(hidden, password.ActualHeight, 1);                 // и «скрыть» тоже
            window.Close();
        });
    }
}
