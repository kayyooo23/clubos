using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using ClubOS.Shared.Ui;

namespace ClubOS.Overlay;

/// <summary>
/// Меню гостя, пока идёт сессия: игры, программы, баланс.
/// Обычный режим (разработка): окно на рабочую область, его можно свернуть, а при запуске игры оно само сворачивается.
/// Режим --kiosk (настоящий ПК гостя): окно на весь основной экран, без рамки, без значка в панели задач, свернуть нельзя ни кнопкой, ни Win+Вниз, ни Alt+Пробел;
/// закрыть тоже нельзя. Оно не «поверх всех»: запущенная игра открывается над ним, а когда игра закрыта, под ней снова меню, а не рабочий стол Windows.
/// Что делать, если игра открылась позади меню или гость вышел на рабочий стол, решает ShellWatcher.
/// </summary>
public partial class LauncherWindow : Window
{
    private const int WmSysCommand = 0x0112, ScMinimize = 0xF020, ScClose = 0xF060;
    private readonly bool _kiosk;

    public bool AllowClose { get; set; }

    /// <summary>Только в киоске: гость запустил игру или программу из меню.</summary>
    public event Action? AppStarted;

    public LauncherWindow(LauncherModel model, bool kiosk = false)
    {
        InitializeComponent();
        _kiosk = kiosk;
        View.DataContext = model;
        WindowStartupLocation = WindowStartupLocation.Manual;
        if (kiosk)
        {
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Left = 0;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;
            SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
            StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; };
            model.AppLaunched += _ => Dispatcher.BeginInvoke(() => AppStarted?.Invoke());
        }
        else
        {
            var area = SystemParameters.WorkArea;
            Left = area.Left;
            Top = area.Top;
            Width = area.Width;
            Height = area.Height;
            model.AppLaunched += _ => Dispatcher.BeginInvoke(() => WindowState = WindowState.Minimized);   // игра на весь экран, меню не мешает
        }
    }

    /// <summary>Сообщения окну: в киоске «свернуть» и «закрыть» из системного меню проглатываются.</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmSysCommand && ((int)wParam & 0xFFF0) is ScMinimize or ScClose) handled = true;
        return IntPtr.Zero;
    }

    /// <summary>Показать меню и вывести его вперёд (в том числе из свёрнутого состояния).</summary>
    public void Bring()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Вернуть меню над всеми обычными окнами и в фокус (гость оказался на рабочем столе или в меню «Пуск»).</summary>
    public void Raise()
    {
        Bring();
        Topmost = true;      // на мгновение «поверх всех»: так окно оказывается над остальными, а потом признак снимается, чтобы игра могла открыться над меню
        Topmost = false;
        Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            if (!_kiosk) WindowState = WindowState.Minimized;
        }
        base.OnClosing(e);
    }
}
