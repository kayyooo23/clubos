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
/// </summary>
public partial class ToastWindow : Window
{
    private const int GwlExStyle = -20, WsExNoActivate = 0x08000000, WsExToolWindow = 0x00000080;
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(15);
    private readonly DispatcherTimer _hide = new() { Interval = Lifetime };

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
        TitleText.Text = warning.Title;
        BodyText.Text = warning.Text;
        if (!IsVisible) Show();
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 20;
        Top = area.Bottom - ActualHeight - 20;
        _hide.Stop();
        _hide.Start();
        if (sound) SystemSounds.Exclamation.Play();
    }

    private void Dismiss()
    {
        _hide.Stop();
        Hide();
    }

    private void Toast_Click(object sender, MouseButtonEventArgs e) => Dismiss();

    /// <summary>Окно не должно переживать сессию: при блокировке ПК уведомление убирается.</summary>
    public void Clear() => Dismiss();
}
