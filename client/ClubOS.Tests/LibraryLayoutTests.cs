using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClubOS.Overlay;
using ClubOS.Shared;
using ClubOS.Shared.Ui;
using Microsoft.Extensions.Time.Testing;

namespace ClubOS.Tests;

/// <summary>Вкладка «Библиотека»: сверху игры, ниже под линией программы. Проверяем настоящее окно, а не только модель.</summary>
public class LibraryLayoutTests
{
    private sealed class NoGames : IAppLauncher
    {
        public bool IsAvailable(AppInfo app) => true;
        public string? Launch(AppInfo app) => null;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    private static (Window Window, LauncherView View, LauncherModel Model) Open(Func<AppInfo[], AppInfo[]>? filter = null)
    {
        var catalog = new[]
        {
            new AppInfo(1, "Dota 2", "games", "MOBA", 12, "steam", "570"),
            new AppInfo(2, "Counter-Strike 2", "games", "Шутеры", 16, "steam", "730"),
            new AppInfo(3, "Apex Legends", "games", "Шутеры", 16, "steam", "1172470"),
            new AppInfo(4, "Chrome", "apps", "Браузеры", null, "exe", @"C:\chrome.exe"),
            new AppInfo(5, "Discord", "apps", "Общение", null, "exe", @"C:\discord.exe"),
        };
        var apps = (filter ?? (a => a))(catalog);
        var agent = new FakeAgent();
        var account = new AccountInfo(2, "guest", "+7 *** ***-00-01", 250);
        var session = new SessionInfo(1, 1, 2, "3 часа", DateTime.UtcNow, null, null, 250, 3600);
        agent.Current = new AgentSnapshot(true, PcMode.GuestSession, account, session, null, 3600, "Demo Club", "PC-1");
        agent.Handler = r => r.Op == Ops.Apps ? agent.Ok(r, apps: apps) : agent.Ok(r);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        var flow = new FlowController(agent, time);
        var model = new LauncherModel(flow, agent, new NoGames(), time, Path.GetTempPath());
        flow.PollAsync().GetAwaiter().GetResult();
        model.PollAsync().GetAwaiter().GetResult();
        var view = new LauncherView { DataContext = model };
        var window = new Window
        {
            Content = view, Width = 1366, Height = 768, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
        };
        window.Show();
        window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => { }));
        view.UpdateLayout();
        return (window, view, model);
    }

    private static TextBlock Header(LauncherView view, string text) =>
        Descendants<TextBlock>(view).First(t => t.Text == text && t.IsVisible);

    private static Rect Bounds(FrameworkElement element, FrameworkElement root) =>
        new(element.TranslatePoint(new Point(0, 0), root), new Size(element.ActualWidth, element.ActualHeight));

    [Fact]
    public void Navigation_has_one_library_item_instead_of_games_and_programs()
    {
        WpfHost.Run(() =>
        {
            var (window, view, _) = Open();
            var all = Descendants<RadioButton>(view).Select(r => r.Content as string).ToList();
            Assert.DoesNotContain("Игры", all);
            Assert.DoesNotContain("Программы", all);
            // в этой сессии продлевать нечем (тарифы клуба не загружены), поэтому вкладки продления в меню нет
            var visible = Descendants<RadioButton>(view).Where(r => r.IsVisible).Select(r => r.Content as string).ToList();
            Assert.Equal(["Библиотека", "Бар"], visible);
            window.Close();
        });
    }

    [Fact]
    public void Programs_stand_below_all_the_game_tiles_under_their_own_heading_with_a_line()
    {
        WpfHost.Run(() =>
        {
            var (window, view, model) = Open();
            var games = Bounds(Header(view, "Игры"), view);
            var programs = Bounds(Header(view, "Программы"), view);
            var lastGameTile = Descendants<Button>(view).Where(b => b.IsVisible && b.DataContext is AppCard c && model.GameCards.Contains(c)).Select(b => Bounds(b, view)).MaxBy(r => r.Bottom);
            var firstProgramTile = Descendants<Button>(view).Where(b => b.IsVisible && b.DataContext is AppCard c && model.AppCards.Contains(c)).Select(b => Bounds(b, view)).MinBy(r => r.Top);

            Assert.True(programs.Top > lastGameTile.Bottom, $"заголовок программ ({programs.Top}) должен быть ниже плиток игр ({lastGameTile.Bottom})");
            Assert.True(firstProgramTile.Top > programs.Bottom - 1, "плитки программ идут под своим заголовком");
            Assert.Equal(games.Left, programs.Left, 1);                              // оба заголовка на одной вертикали

            // линия справа от заголовка: на одной высоте с ним и тянется вправо
            var lines = Descendants<System.Windows.Shapes.Rectangle>(view).Where(r => r.IsVisible && r.Height == 2 && r.ActualWidth > 200).Select(r => Bounds(r, view)).ToList();
            Assert.Contains(lines, l => Math.Abs((l.Top + l.Height / 2) - (games.Top + games.Height / 2)) < 20 && l.Left > games.Right);
            Assert.Contains(lines, l => Math.Abs((l.Top + l.Height / 2) - (programs.Top + programs.Height / 2)) < 20 && l.Left > programs.Right);
            window.Close();
        });
    }

    [Fact]
    public void There_are_no_category_chips_in_the_library_and_the_programs_heading_is_missing_when_there_are_no_programs()
    {
        WpfHost.Run(() =>
        {
            var (window, view, _) = Open(a => a.Where(x => x.Tab == "games").ToArray());
            Assert.DoesNotContain(Descendants<TextBlock>(view), t => t.Text == "Программы" && t.IsVisible);
            Assert.DoesNotContain(Descendants<Button>(view), b => b.IsVisible && b.Content is "MOBA" or "Шутеры" or "Все");   // ряда категорий нет
            window.Close();
        });
    }
}
