using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClubOS.Overlay;

namespace ClubOS.Tests;

/// <summary>Вопрос «Завершить сессию?» в оформлении ClubOS (а не системное окно Windows).</summary>
public class ConfirmWindowTests
{
    private static ConfirmWindow Open()
    {
        var window = new ConfirmWindow("Завершить сессию?", "Оставшееся время пакета не возвращается.", "Завершить", "Остаться")
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false,
        };
        window.Show();
        window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => { }));
        return window;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [Fact]
    public void Shows_the_question_with_two_clear_buttons_in_the_app_style()
    {
        WpfHost.Run(() =>
        {
            var window = Open();
            var texts = Descendants<TextBlock>(window).Select(t => t.Text).ToList();
            Assert.Contains("Завершить сессию?", texts);
            Assert.Contains("Оставшееся время пакета не возвращается.", texts);
            Assert.Equal(["Завершить", "Остаться"], Descendants<Button>(window).Select(b => (string)b.Content));
            Assert.Equal(WindowStyle.None, window.WindowStyle);                                     // не системная рамка Windows
            Assert.False(window.ShowInTaskbar);
            var yesButton = (Button)window.FindName("YesButton");
            yesButton.ApplyTemplate();
            var box = (Border)yesButton.Template.FindName("Box", yesButton);
            Assert.Equal(Colors.White, ((SolidColorBrush)box.Background).Color);                   // главная кнопка белая, как везде
            window.Close();
        });
    }

    [Fact]
    public void Pressing_the_main_button_confirms()
    {
        WpfHost.Run(() =>
        {
            var window = Open();
            Click((Button)window.FindName("YesButton"));
            Assert.True(window.Confirmed);
            Assert.False(window.IsVisible);
        });
    }

    [Fact]
    public void Pressing_the_second_button_declines()
    {
        WpfHost.Run(() =>
        {
            var window = Open();
            Click((Button)window.FindName("NoButton"));
            Assert.False(window.Confirmed);
            Assert.False(window.IsVisible);
        });
    }

    [Fact]
    public void Escape_declines()
    {
        WpfHost.Run(() =>
        {
            var window = Open();
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            window.RaiseEvent(key);
            Assert.False(window.Confirmed);
            Assert.False(window.IsVisible);
        });
    }

    [Fact]
    public void A_window_closed_without_an_answer_counts_as_no()
    {
        WpfHost.Run(() =>
        {
            var window = Open();
            window.Close();
            Assert.False(window.Confirmed);
        });
    }
}
