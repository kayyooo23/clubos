using System.Windows;
using ClubOS.Overlay;
using ClubOS.Shared;
using ClubOS.Shared.Ui;
using Microsoft.Extensions.Time.Testing;

namespace ClubOS.Tests;

/// <summary>Меню гостя в режиме --kiosk занимает весь экран и не сворачивается; в обычном режиме остаётся обычным окном. Проверяем настоящие окна (за краем экрана).</summary>
public class KioskMenuTests
{
    private sealed class Games : IAppLauncher
    {
        public bool IsAvailable(AppInfo app) => true;
        public string? Launch(AppInfo app) => null;
    }

    private static LauncherModel InSession()
    {
        var agent = new FakeAgent();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));
        agent.Handler = r => r.Op switch
        {
            Ops.Tariffs => agent.Ok(r, tariffs: []),
            Ops.Apps => agent.Ok(r, apps: [new AppInfo(1, "Dota 2", "games", "MOBA", 12, "steam", "570")]),
            _ => agent.Ok(r),
        };
        agent.Current = new AgentSnapshot(true, PcMode.GuestSession, new AccountInfo(2, "guest", "+7 *** ***-00-01", 250),
            new SessionInfo(1, 1, 2, "3 часа", DateTime.UtcNow, null, null, 250, 3600), null, 3600);
        var flow = new FlowController(agent, time);
        var model = new LauncherModel(flow, agent, new Games(), time);
        flow.PollAsync().GetAwaiter().GetResult();
        model.PollAsync().GetAwaiter().GetResult();
        return model;
    }

    private static void ShowOffscreen(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        window.ShowActivated = false;
        window.Show();
        Pump(window);
    }

    private static void Pump(Window window) =>
        window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => { }));

    [Fact]
    public void The_kiosk_menu_fills_the_screen_has_no_taskbar_button_and_cannot_be_minimized()
    {
        WpfHost.Run(() =>
        {
            var window = new LauncherWindow(InSession(), kiosk: true);
            Assert.False(window.ShowInTaskbar);
            Assert.Equal(ResizeMode.NoResize, window.ResizeMode);
            Assert.False(window.Topmost);                                      // игра, которую запустит гость, открывается над меню
            Assert.Equal((0.0, 0.0), (window.Left, window.Top));
            Assert.Equal((SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight), (window.Width, window.Height));
            ShowOffscreen(window);

            window.WindowState = WindowState.Minimized;
            Pump(window);
            Assert.Equal(WindowState.Normal, window.WindowState);              // свернуть нельзя: окно возвращается само

            window.AllowClose = true;
            window.Close();
        });
    }

    [Fact]
    public void Closing_the_kiosk_menu_does_nothing()
    {
        WpfHost.Run(() =>
        {
            var window = new LauncherWindow(InSession(), kiosk: true);
            ShowOffscreen(window);
            window.Close();                                                    // Alt+F4 и подобное
            Pump(window);
            Assert.True(window.IsVisible);
            Assert.Equal(WindowState.Normal, window.WindowState);
            window.AllowClose = true;
            window.Close();
        });
    }

    [Fact]
    public void The_development_menu_stays_an_ordinary_window_that_can_be_minimized()
    {
        WpfHost.Run(() =>
        {
            var window = new LauncherWindow(InSession());
            Assert.True(window.ShowInTaskbar);
            Assert.Equal(ResizeMode.CanMinimize, window.ResizeMode);
            window.AllowClose = true;
        });
    }

    [Fact]
    public void Starting_a_game_in_kiosk_keeps_the_menu_in_place_and_tells_the_watcher()
    {
        WpfHost.Run(() =>
        {
            var model = InSession();
            var window = new LauncherWindow(model, kiosk: true);
            var started = 0;
            window.AppStarted += () => started++;
            ShowOffscreen(window);
            model.Launch(model.GameCards[0]);
            Pump(window);
            Assert.Equal(1, started);
            Assert.Equal(WindowState.Normal, window.WindowState);              // не сворачивается: под игрой должно оставаться меню, а не рабочий стол
            window.AllowClose = true;
            window.Close();
        });
    }

    [Fact]
    public void Starting_a_game_in_development_still_minimizes_the_menu()
    {
        WpfHost.Run(() =>
        {
            var model = InSession();
            var window = new LauncherWindow(model);
            ShowOffscreen(window);
            model.Launch(model.GameCards[0]);
            Pump(window);
            Assert.Equal(WindowState.Minimized, window.WindowState);
            window.AllowClose = true;
            window.Close();
        });
    }

    // ---------- сообщение на весь экран ----------
    [Fact]
    public void The_full_screen_notice_shows_the_text_adds_a_second_message_and_hides_on_dismiss()
    {
        WpfHost.Run(() =>
        {
            var window = new NoticeWindow { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false };
            window.Present("Пожалуйста, постарайтесь вести себя потише.", returnTo: 0, sound: false, takeFocus: false);
            Pump(window);
            Assert.True(window.IsVisible);
            Assert.Equal("Пожалуйста, постарайтесь вести себя потише.", window.Text);

            window.Present("Подойдите к администратору.", returnTo: 0, sound: false, takeFocus: false);
            Assert.Equal("Пожалуйста, постарайтесь вести себя потише.\n\nПодойдите к администратору.", window.Text);

            window.Dismiss();
            Assert.False(window.IsVisible);
            window.AllowClose = true;
            window.Close();
        });
    }

    [Fact]
    public void The_full_screen_notice_cannot_be_closed_by_alt_f4_only_hidden()
    {
        WpfHost.Run(() =>
        {
            var window = new NoticeWindow { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false };
            window.Present("Тише.", returnTo: 0, sound: false, takeFocus: false);
            window.Close();
            Assert.False(window.IsVisible);                                    // спрятано, а не уничтожено: следующее сообщение покажется снова
            window.Present("Ещё раз.", returnTo: 0, sound: false, takeFocus: false);
            Assert.True(window.IsVisible);
            window.AllowClose = true;
            window.Close();
        });
    }
}
