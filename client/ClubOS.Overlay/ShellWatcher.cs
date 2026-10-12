using System.Windows.Threading;
using ClubOS.Shared.Ui;

namespace ClubOS.Overlay;

/// <summary>
/// Следит за окнами, пока идёт сессия гостя в режиме --kiosk (см. ShellGuard): возвращает меню, когда на переднем плане рабочий стол, панель задач
/// или «Пуск»; выводит вперёд только что запущенную игру, если она открылась позади меню; закрывает окна Проводника с папками.
/// Опрос раз в 0,4 секунды: достаточно, чтобы гость не успел ничем воспользоваться, и почти не нагружает ПК.
/// </summary>
public sealed class ShellWatcher : IDisposable
{
    private readonly LauncherWindow _menu;
    private readonly ShellGuard _guard = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    public ShellWatcher(LauncherWindow menu)
    {
        _menu = menu;
        _timer.Tick += (_, _) => Tick();
    }

    public void Start()
    {
        if (_timer.IsEnabled) return;
        _guard.Reset();
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        _guard.Reset();
    }

    /// <summary>Гость запустил игру: ближайшие полторы минуты окно, открывшееся позади меню, выводим вперёд.</summary>
    public void Launched() => _guard.Launched(DateTimeOffset.UtcNow, NativeWindows.TopLevel().Select(w => w.Handle));

    private void Tick()
    {
        try
        {
            var decision = _guard.Decide(NativeWindows.Foreground(), NativeWindows.TopLevel(), NativeWindows.OwnPid, DateTimeOffset.UtcNow);
            switch (decision.Action)
            {
                case GuardAction.ShowMenu:
                    _menu.Raise();
                    break;
                case GuardAction.PullToFront:
                    Log.Info("Игра открылась позади меню: выводим вперёд");
                    NativeWindows.BringToFront(decision.Handle);
                    break;
                case GuardAction.CloseWindow:
                    Log.Info("Гость открыл окно Проводника: закрываем");
                    NativeWindows.Close(decision.Handle);
                    break;
            }
        }
        catch (Exception e) { Log.Error("Сбой слежения за окнами", e); }
    }

    public void Dispose() => Stop();
}
