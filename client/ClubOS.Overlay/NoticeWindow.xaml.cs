using System.ComponentModel;
using System.Media;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ClubOS.Overlay;

/// <summary>
/// Сообщение администратора на весь экран (см. NoticePresenter): появляется, когда поверх чужого полноэкранного приложения маленькое окно в углу не видно.
/// Забирает фокус, поэтому полноэкранная игра сворачивается, и гость точно прочитает. «Понятно» (Enter, Esc, щелчок) закрывает окно и возвращает фокус
/// игре, с которой его забрали. Не нажал за минуту: закрывается само. Второе сообщение, пока первое открыто, дописывается ниже.
/// </summary>
public partial class NoticeWindow : Window
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);
    private readonly DispatcherTimer _hide = new() { Interval = Lifetime };
    private long _returnTo;

    public bool AllowClose { get; set; }

    public NoticeWindow()
    {
        InitializeComponent();
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        _hide.Tick += (_, _) => Dismiss();
    }

    public string Text => BodyText.Text;

    /// <summary>Показать сообщение. returnTo: окно (чужая игра), которому вернуть фокус после «Понятно»; 0, если возвращать некому. takeFocus: false только в тестах.</summary>
    public void Present(string text, long returnTo, bool sound = true, bool takeFocus = true)
    {
        if (IsVisible)
        {
            BodyText.Text += "\n\n" + text;
        }
        else
        {
            BodyText.Text = text;
            _returnTo = returnTo;
            Show();
        }
        _hide.Stop();
        _hide.Start();
        var handle = new WindowInteropHelper(this).Handle;
        if (takeFocus && handle != IntPtr.Zero) NativeWindows.BringToFront(handle.ToInt64());   // забрать фокус у игры
        if (sound) SystemSounds.Exclamation.Play();
    }

    /// <summary>Закрыть окно и вернуть фокус игре, у которой его забрали.</summary>
    public void Dismiss()
    {
        _hide.Stop();
        if (!IsVisible) return;
        Hide();
        if (_returnTo != 0) NativeWindows.BringToFront(_returnTo);   // вернуть игру на экран и в фокус
        _returnTo = 0;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Dismiss();

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.Enter or Key.Space) { e.Handled = true; Dismiss(); }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose) { e.Cancel = true; Dismiss(); }   // Alt+F4 только прячет окно
        base.OnClosing(e);
    }
}
