using System.Windows;
using System.Windows.Threading;
using ClubOS.Shared;
using ClubOS.Shared.Ui;

namespace ClubOS.Overlay;

/// <summary>
/// Решает, какое окно сейчас показывать, по режиму ПК:
///   locked        → экран блокировки (в киоске — поверх всего, с перехватом клавиш и возвратом фокуса);
///   guest_session → скрыт экран блокировки, виден только обычное окно меню (игры, баланс, время), клавиши свободны;
///   staff_desktop → ничего не закрывает, в трее значок «Заблокировать ПК».
/// Пока служба не ответила ни разу, ПК закрыт (экран блокировки виден с самого старта).
/// </summary>
public sealed class WindowCoordinator : IDisposable
{
    private readonly FlowController _flow;
    private readonly Options _options;
    private readonly LockWindow _lock;
    private readonly LauncherWindow _launcher;
    private readonly LauncherModel _model;
    private readonly ToastWindow _toast = new();
    private readonly SessionWarnings _warnings = new();
    private readonly TrayController _tray;
    private readonly KioskGuard _kiosk;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _focus = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private PcMode? _mode;

    public WindowCoordinator(FlowController flow, LauncherModel model, Options options)
    {
        _flow = flow;
        _model = model;
        _options = options;
        _lock = new LockWindow(flow, model, options.Kiosk);
        _launcher = new LauncherWindow(model);
        _tray = new TrayController(flow);
        _kiosk = new KioskGuard(options.AllowExit);
    }

    public void Start()
    {
        _flow.SnapshotChanged += Apply;
        _flow.SnapshotChanged += WarnIfEnding;
        _kiosk.ExitRequested += () => Application.Current.Dispatcher.Invoke(Exit);
        _lock.Closed += (_, _) => { if (!_options.Kiosk) Exit(); };   // в режиме разработки закрытие окна = выход

        _poll.Tick += async (_, _) =>
        {
            try
            {
                await _flow.PollAsync();
                await _model.PollAsync();
            }
            catch (Exception e) { Log.Error("Сбой опроса службы", e); }
        };
        _focus.Tick += (_, _) => KeepLockOnTop();

        ShowLock();                       // с первой секунды ПК закрыт, пока служба не скажет иное
        _poll.Start();
        Log.Info($"Оверлей запущен ({(_options.Kiosk ? "киоск" : "режим разработки")}), канал {_options.PipeName}");
    }

    private void Apply(AgentSnapshot s)
    {
        if (_mode == s.Mode) return;
        Log.Info($"Режим ПК: {_mode?.ToString() ?? "—"} → {s.Mode}");
        var guestJustLeft = _mode == PcMode.GuestSession && s.Mode == PcMode.Locked;
        _mode = s.Mode;
        if (guestJustLeft) ClearClipboard();   // скопированный гостем пароль или текст не должен достаться следующему
        switch (s.Mode)
        {
            case PcMode.Locked:
                _launcher.Hide();
                _tray.Hide();
                ShowLock();
                break;
            case PcMode.GuestSession:
                HideLock();
                _tray.Hide();
                _launcher.Bring();   // сессия началась: показываем меню с играми
                break;
            case PcMode.StaffDesktop:
                HideLock();
                _launcher.Hide();
                _tray.Show();
                break;
        }
    }

    /// <summary>За 15, 10 и 5 минут до конца времени показываем короткое уведомление со звуком (см. SessionWarnings).</summary>
    private void WarnIfEnding(AgentSnapshot s)
    {
        var warning = _warnings.Check(s);
        if (warning is not null)
        {
            Log.Info($"Предупреждение гостю: {warning.Title}, {warning.Minutes} мин");
            _toast.ShowWarning(warning);
        }
        if (s.Mode != PcMode.GuestSession) _toast.Clear();   // сессия кончилась: уведомление не должно остаться на экране блокировки
    }

    private static void ClearClipboard()
    {
        try { Clipboard.Clear(); }
        catch (System.Runtime.InteropServices.ExternalException e) { Log.Error("Не удалось очистить буфер обмена", e); }   // буфер занят другой программой
    }

    private void ShowLock()
    {
        if (!_lock.IsVisible) _lock.Show();
        _lock.Activate();
        _lock.FocusFirstField();
        if (_options.Kiosk)
        {
            _kiosk.Enable();
            _focus.Start();
        }
    }

    private void HideLock()
    {
        _kiosk.Disable();   // во время игры и на рабочем столе сотрудника все клавиши должны работать
        _focus.Stop();
        _lock.Hide();
    }

    /// <summary>В киоске экран блокировки всегда впереди и в фокусе: если другое окно перехватило фокус, возвращаем его.</summary>
    private void KeepLockOnTop()
    {
        if (!_lock.IsVisible) return;
        if (!_lock.IsActive)
        {
            _lock.Topmost = false;
            _lock.Topmost = true;
            _lock.Activate();
        }
    }

    private void Exit()
    {
        Log.Info("Выход оверлея");
        _lock.AllowClose = true;
        _launcher.AllowClose = true;
        _toast.Close();
        Dispose();
        Application.Current.Shutdown();
    }

    public void Dispose()
    {
        _poll.Stop();
        _focus.Stop();
        _kiosk.Dispose();
        _tray.Dispose();
    }
}
