using System.Windows;
using System.Windows.Input;

namespace ClubOS.Overlay;

/// <summary>Вопрос с двумя ответами в оформлении ClubOS. Ask возвращает true, только если гость нажал главную кнопку; Esc и закрытие окна считаются отказом.</summary>
public partial class ConfirmWindow : Window
{
    public bool Confirmed { get; private set; }

    public ConfirmWindow(string title, string message, string yes, string no)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        YesButton.Content = yes;
        NoButton.Content = no;
    }

    public static bool Ask(Window? owner, string title, string message, string yes, string no)
    {
        var window = new ConfirmWindow(title, message, yes, no);
        if (owner is { IsVisible: true }) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowDialog();
        return window.Confirmed;
    }

    private void Yes_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        Close();
    }

    private void No_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        base.OnPreviewKeyDown(e);
    }
}
