using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using ClubOS.Shared.Ui;

namespace ClubOS.Overlay;

/// <summary>
/// Уведомление в правом нижнем углу поверх всех окон: «через 5 минут тариф закончится». Маленькое, не забирает фокус
/// у игры (окно не активируется даже по щелчку), гаснет само через 15 секунд. Сопровождается коротким звуком.
/// Поверх игры в «настоящем» полноэкранном режиме Windows окна не показывает: в такой игре нужен режим «в окне без рамки».
/// Так же показывается сообщение администратора («Пожалуйста, постарайтесь вести себя потише»): дольше, и оно не исчезает само
/// при смене режима ПК, пока гость его не прочёл.
/// </summary>
public partial class ToastWindow : Window
{
    private const int GwlExStyle = -20, WsExNoActivate = 0x08000000, WsExToolWindow = 0x00000080;
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan NoticeLifetime = TimeSpan.FromSeconds(30);
    private readonly DispatcherTimer _hide = new() { Interval = Lifetime };
    private bool _isNotice;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    public ToastWindow()
    {
        InitializeComponent();
        _hide.Tick += (_, _) => Dismiss();
        SourceInitialized += (_, _) =>
        {
            // NOACTIVATE: щелчок по уведомлению не переключает фокус с игры; TOOLWINDOW: нет в Alt+Tab
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, GwlExStyle, GetWindowLong(hwnd, GwlExStyle) | WsExNoActivate | WsExToolWindow);
        };
    }

    public void ShowWarning(SessionWarning warning, bool sound = true)
    {
        if (_isNotice && IsVisible) return;   // сообщение администратора ещё читают: предупреждение о времени придёт снова на следующей отметке
        Present(warning.Title, warning.Text, Lifetime, notice: false, sound);
    }

    /// <summary>Сообщение администратора гостю: любой текст, поверх всех окон, со звуком.</summary>
    public void ShowNotice(string text, bool sound = true) =>
        Present("Сообщение от администратора", text, NoticeLifetime, notice: true, sound);

    private void Present(string title, string body, TimeSpan lifetime, bool notice, bool sound)
    {
        _isNotice = notice;
        TitleText.Text = title;
        BodyText.Text = body;
        if (!IsVisible) Show();
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 20;
        Top = area.Bottom - ActualHeight - 20;
        _hide.Stop();
        _hide.Interval = lifetime;
        _hide.Start();
        if (sound) SystemSounds.Exclamation.Play();
    }

    private void Dismiss()
    {
        _hide.Stop();
        _isNotice = false;
        Hide();
    }

    private void Toast_Click(object sender, MouseButtonEventArgs e) => Dismiss();

    /// <summary>Окно не должно переживать сессию: при блокировке ПК предупреждение убирается. Сообщение администратора остаётся, пока его не закроют или оно не погаснет само.</summary>
    public void Clear()
    {
        if (!_isNotice) Dismiss();
    }
}
