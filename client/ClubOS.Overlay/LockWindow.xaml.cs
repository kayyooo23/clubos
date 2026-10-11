using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClubOS.Shared.Ui;

namespace ClubOS.Overlay;

/// <summary>
/// Экран блокировки: вход, регистрация, сброс пароля, а когда гость вошёл, его меню (LauncherView). В режиме --kiosk закрывает весь основной экран
/// поверх всего; в обычном (разработка) — просто окно, которое ничего не блокирует.
/// </summary>
public partial class LockWindow : Window
{
    private readonly FlowController _flow;
    private readonly AttractModel _attract;
    private bool _syncingPasswords;

    /// <summary>Закрыть окно пользователь может только в режиме разработки или по аварийной комбинации.</summary>
    public bool AllowClose { get; set; }

    public LockWindow(FlowController flow, LauncherModel launcher, bool kiosk)
    {
        InitializeComponent();
        _flow = flow;
        DataContext = flow;
        _attract = launcher.Attract;
        Attract.DataContext = _attract;      // у заставки своя модель; её Visibility привязана к ней же
        _attract.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AttractModel.IsAttract) && !_attract.IsAttract) Dispatcher.BeginInvoke(FocusFirstField); };
        Launcher.DataContext = launcher;   // меню гостя (после входа) берёт данные у своей модели, а не у FlowController напрямую
        if (kiosk)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = 0;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;
        }
        else
        {
            Title = "ClubOS - режим разработки (ПК не блокируется)";
            Width = 1280;
            Height = 820;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            AllowClose = true;
        }
        flow.PropertyChanged += OnFlowChanged;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose) e.Cancel = true;   // Alt+F4 и подобное не закрывают экран блокировки
        base.OnClosing(e);
    }

    // ---------- синхронизация с контроллером ----------
    private void OnFlowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FlowController.Password) && _flow.Password.Length == 0) ClearBoxes(1);
        if (e.PropertyName == nameof(FlowController.Password2) && _flow.Password2.Length == 0) ClearBoxes(2);
        if (e.PropertyName == nameof(FlowController.Screen)) Dispatcher.BeginInvoke(FocusFirstField);
    }

    private void ClearBoxes(int which)
    {
        _syncingPasswords = true;
        try
        {
            foreach (var box in new[] { PwdLogin, PwdReg1, PwdReg2, PwdReset1, PwdReset2 })
                if ((string)box.Tag == which.ToString() && box.Password.Length > 0) { box.Password = ""; Input.Conceal(box); }
        }
        finally { _syncingPasswords = false; }
    }

    private void Password_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingPasswords) return;
        var box = (PasswordBox)sender;
        if ((string)box.Tag == "1") _flow.Password = box.Password; else _flow.Password2 = box.Password;
    }

    public void FocusFirstField()
    {
        Control? target = _flow.Screen switch
        {
            Screen.Login => _flow.Phone.Length == 0 ? LoginPhone : PwdLogin,
            Screen.RegisterPhone => RegPhone,
            Screen.RegisterConfirm => RegCode,
            Screen.ResetPhone => ResetPhone,
            Screen.ResetConfirm => ResetCode,
            _ => null,
        };
        target?.Focus();
    }

    // ---------- ввод ----------
    private void Window_Move(object sender, MouseEventArgs e)
    {
        _flow.Touch();
        var at = e.GetPosition(this);
        _attract.Input(InputKind.Move, at.X, at.Y);
    }

    private void Window_Down(object sender, MouseButtonEventArgs e)
    {
        _flow.Touch();
        if (_attract.Input(InputKind.Click)) e.Handled = true;   // клик, разбудивший заставку, в форму под ней не попадает
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        _flow.Touch();
        if (_attract.Input(InputKind.Key)) { e.Handled = true; return; }   // клавиша, разбудившая заставку, в поле не попадает
        if (e.Key != Key.Enter || !_flow.CanInteract) return;
        e.Handled = true;
        switch (_flow.Screen)
        {
            case Screen.Login: Login_Click(sender, e); break;
            case Screen.RegisterPhone or Screen.ResetPhone: GetCode_Click(sender, e); break;
            case Screen.RegisterConfirm or Screen.ResetConfirm: Confirm_Click(sender, e); break;
        }
    }

    // ---------- кнопки ----------
    private static async void Run(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { Log.Error("Сбой в обработчике кнопки", ex); }
    }

    private void Login_Click(object sender, RoutedEventArgs e) => Run(_flow.LoginAsync);
    private void GetCode_Click(object sender, RoutedEventArgs e) => Run(_flow.RequestCodeAsync);
    private void Confirm_Click(object sender, RoutedEventArgs e) => Run(_flow.ConfirmAsync);
    private void ToRegister_Click(object sender, RoutedEventArgs e) => _flow.GoTo(Screen.RegisterPhone);
    private void ToReset_Click(object sender, RoutedEventArgs e) => _flow.GoTo(Screen.ResetPhone);
    private void BackToLogin_Click(object sender, RoutedEventArgs e) => _flow.GoTo(Screen.Login);
}
