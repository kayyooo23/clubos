using System.Collections.Concurrent;
using System.Windows;
using ClubOS.Overlay;
using ClubOS.Shared;
using ClubOS.Shared.Ui;

namespace ClubOS.Tests;

/// <summary>Одна постоянная «поточная» нить для WPF: приложение WPF одно на процесс и живёт в нити, где создано.</summary>
internal static class WpfHost
{
    private static readonly BlockingCollection<Action> Queue = new();

    static WpfHost()
    {
        var thread = new Thread(() =>
        {
            new ClubOS.Overlay.App().InitializeComponent();   // стили и цвета окон лежат в App.xaml
            foreach (var work in Queue.GetConsumingEnumerable()) work();
        }) { IsBackground = true, Name = "WPF для тестов" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public static void Run(Action action)
    {
        Exception? error = null;
        using var done = new ManualResetEventSlim();
        Queue.Add(() =>
        {
            try { action(); } catch (Exception e) { error = e; } finally { done.Set(); }
        });
        done.Wait();
        if (error is not null) throw new InvalidOperationException("Тест окна упал: " + error.Message, error);
    }
}

public class LockWindowTests
{
    /// <summary>Привязки WPF оживают, только когда окно показано: показываем его далеко за краем экрана и даём обработать очередь сообщений.</summary>
    private static LockWindow ShowOffscreen(FlowController flow, LauncherModel model)
    {
        var window = new LockWindow(flow, model, kiosk: false)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
        };
        window.Show();
        Pump(window);
        return window;
    }

    private static void Pump(Window window) =>
        window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => { }));

    private sealed class NoGames : IAppLauncher
    {
        public bool IsAvailable(AppInfo app) => true;
        public string? Launch(AppInfo app) => null;
    }

    private static AgentSnapshot Locked() => new(true, PcMode.Locked, null, null, null, null);
    private static AgentSnapshot Cabinet() =>
        new(true, PcMode.Locked, new AccountInfo(2, "guest", "+7 *** ***-00-01", 500), null, null, null);

    [Fact]
    public void Login_form_is_shown_until_the_guest_is_in_and_then_the_menu_replaces_it()
    {
        // Регрессия: меню получило собственный DataContext, привязка видимости перестала находить Screen,
        // и WPF показывал меню ВСЕГДА поверх формы входа. Проверяем настоящее окно, а не только логику.
        WpfHost.Run(() =>
        {
            var agent = new FakeAgent();
            agent.Handler = r => agent.Ok(r, tariffs: [], apps: []);
            agent.Current = Locked();
            var flow = new FlowController(agent);
            var model = new LauncherModel(flow, agent, new NoGames());
            var window = ShowOffscreen(flow, model);
            var auth = (FrameworkElement)window.FindName("AuthGrid");
            var menu = (FrameworkElement)window.FindName("Launcher");

            Assert.Equal(Visibility.Visible, auth.Visibility);          // до первого опроса
            Assert.Equal(Visibility.Collapsed, menu.Visibility);

            flow.PollAsync().GetAwaiter().GetResult();                   // ПК закрыт, гость не вошёл
            Pump(window);
            Assert.Equal(Visibility.Visible, auth.Visibility);
            Assert.Equal(Visibility.Collapsed, menu.Visibility);

            agent.Current = Cabinet();                                   // гость вошёл
            flow.PollAsync().GetAwaiter().GetResult();
            Pump(window);
            Assert.Equal(Visibility.Collapsed, auth.Visibility);
            Assert.Equal(Visibility.Visible, menu.Visibility);

            agent.Current = Locked();                                    // вышел
            flow.PollAsync().GetAwaiter().GetResult();
            Pump(window);
            Assert.Equal(Visibility.Visible, auth.Visibility);
            Assert.Equal(Visibility.Collapsed, menu.Visibility);
            window.AllowClose = true;
            window.Close();
        });
    }

    [Fact]
    public void Registration_screens_are_reachable_and_hide_each_other()
    {
        WpfHost.Run(() =>
        {
            var agent = new FakeAgent();
            agent.Handler = r => agent.Ok(r, tariffs: []);
            agent.Current = Locked();
            var flow = new FlowController(agent);
            var window = ShowOffscreen(flow, new LauncherModel(flow, agent, new NoGames()));
            flow.PollAsync().GetAwaiter().GetResult();
            Pump(window);

            foreach (var screen in new[] { Screen.RegisterPhone, Screen.ResetPhone, Screen.Login })
            {
                flow.GoTo(screen);
                Pump(window);
                var menu = (FrameworkElement)window.FindName("Launcher");
                Assert.Equal(Visibility.Collapsed, menu.Visibility);
                Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("AuthGrid")).Visibility);
            }
            window.AllowClose = true;
            window.Close();
        });
    }
}
