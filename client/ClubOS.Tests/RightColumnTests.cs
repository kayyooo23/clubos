using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClubOS.Overlay;
using ClubOS.Shared;
using ClubOS.Shared.Ui;
using Microsoft.Extensions.Time.Testing;
using Ellipse = System.Windows.Shapes.Ellipse;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace ClubOS.Tests;

/// <summary>Правая колонка меню: сколько осталось времени, баланс, кнопки (продлить, пополнить, вызвать администратора, завершить). Проверяем настоящее окно.</summary>
public class RightColumnTests
{
    private sealed class NoGames : IAppLauncher
    {
        public bool IsAvailable(AppInfo app) => true;
        public string? Launch(AppInfo app) => null;
    }

    private static readonly IReadOnlyList<TariffInfo> Tariffs =
    [
        new(1, "Час", "hourly", 120, null, null, null),
        new(2, "3 часа", "package", 250, 180, null, null),
    ];

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    private static Rect Bounds(FrameworkElement element, FrameworkElement root) =>
        new(element.TranslatePoint(new Point(0, 0), root), new Size(element.ActualWidth, element.ActualHeight));

    /// <summary>kind: тип стартового тарифа сессии; null: сессии нет (гость в кабинете).</summary>
    private static (Window Window, LauncherView View, LauncherModel Model) Open(string? kind, bool overtime = false, LauncherPage? page = null)
    {
        var agent = new FakeAgent();
        var account = new AccountInfo(2, "guest", "+7 *** ***-00-01", 250);
        agent.Current = kind is null
            ? new AgentSnapshot(true, PcMode.Locked, account, null, null, null, "Demo Club", "PC-1")
            : new AgentSnapshot(true, PcMode.GuestSession, account,
                new SessionInfo(1, 1, 2, "3 часа", DateTime.UtcNow, null, null, 250, 9100, Kind: kind, Overtime: overtime), null, 9100, "Demo Club", "PC-1");
        agent.Handler = r => r.Op switch
        {
            Ops.Tariffs => agent.Ok(r, tariffs: Tariffs),
            Ops.Apps => agent.Ok(r, apps: []),
            Ops.Products => new IpcResponse(r.Id, true, 200, null, agent.Current, Products: [new ProductInfo(1, "Кола", "Напитки", 90)]),
            _ => agent.Ok(r),
        };
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        var flow = new FlowController(agent, time);
        var model = new LauncherModel(flow, agent, new NoGames(), time, Path.GetTempPath());
        flow.PollAsync().GetAwaiter().GetResult();
        model.PollAsync().GetAwaiter().GetResult();
        if (page is { } p)
        {
            model.SelectPage(p);
            model.PollAsync().GetAwaiter().GetResult();
            if (p == LauncherPage.Bar) model.Bar.Add(1);
        }
        var view = new LauncherView { DataContext = model };
        var window = new Window
        {
            Content = view, Width = 1920, Height = 1080, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
        };
        window.Show();
        window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => { }));
        view.UpdateLayout();
        return (window, view, model);
    }

    private static List<(string Text, Rect Box, Button Button)> RightButtons(LauncherView view)
    {
        var column = view.ActualWidth - 340;
        // плитки действий подписаны коротко («Продлить»), полное название лежит в AutomationProperties.Name
        string Name(Button b) => System.Windows.Automation.AutomationProperties.GetName(b) is { Length: > 0 } n ? n : (string)b.Content;
        return Descendants<Button>(view).Where(b => b.IsVisible && (b.Content is string || System.Windows.Automation.AutomationProperties.GetName(b).Length > 0) && Bounds(b, view).Left >= column - 1)
            .Select(b => (Name(b), Bounds(b, view), b)).OrderBy(x => x.Item2.Top).ToList();
    }

    [Fact]
    public void A_package_session_shows_the_time_left_then_the_balance_then_all_four_buttons_in_the_right_column()
    {
        WpfHost.Run(() =>
        {
            var (window, view, _) = Open("package");
            var column = view.ActualWidth - 340;
            var label = Descendants<TextBlock>(view).Single(t => t.Text == "Осталось" && t.IsVisible);
            var clock = (TextBlock)view.FindName("HeaderClock");
            var balanceLabel = Descendants<TextBlock>(view).Single(t => t.Text == "Баланс" && t.IsVisible);
            var buttons = RightButtons(view);

            Assert.Equal("2:31:40", clock.Text);
            Assert.True(Bounds(clock, view).Left >= column, "время теперь справа, а не в шапке центра");
            Assert.True(Bounds(label, view).Bottom <= Bounds(clock, view).Top + 6, "подпись «ОСТАЛОСЬ» над временем");
            Assert.True(Bounds(clock, view).Bottom <= Bounds(balanceLabel, view).Top, "баланс ниже времени");
            Assert.Equal(["Продлить тариф", "Пополнить баланс", "Вызвать администратора", "Завершить сессию"], buttons.Select(b => b.Text));
            Assert.True(buttons[0].Box.Top > Bounds(balanceLabel, view).Bottom, "кнопки ниже баланса");
            window.Close();
        });
    }

    [Fact]
    public void The_header_of_the_center_no_longer_carries_the_session_clock()
    {
        WpfHost.Run(() =>
        {
            var (window, view, _) = Open("package");
            var column = view.ActualWidth - 340;
            var inCenter = Descendants<TextBlock>(view).Where(t => t.IsVisible && t.Text is "Идёт сессия" or "2:31:40" && Bounds(t, view).Left < column).ToList();
            Assert.Empty(inCenter);
            window.Close();
        });
    }

    [Fact]
    public void After_the_tariff_ended_the_label_says_how_long_the_balance_lasts()
    {
        WpfHost.Run(() =>
        {
            var (window, view, _) = Open("package", overtime: true);
            Assert.Contains(Descendants<TextBlock>(view), t => t.Text == "Хватит баланса на" && t.IsVisible);
            Assert.DoesNotContain(Descendants<TextBlock>(view), t => t.Text == "Осталось" && t.IsVisible);
            window.Close();
        });
    }

    [Fact]
    public void An_hourly_session_has_no_extend_button_but_keeps_the_rest()
    {
        WpfHost.Run(() =>
        {
            var (window, view, _) = Open("hourly");
            Assert.Equal(["Пополнить баланс", "Вызвать администратора", "Завершить сессию"], RightButtons(view).Select(b => b.Text));
            window.Close();
        });
    }

    [Fact]
    public void Before_a_session_there_is_no_time_no_extend_and_no_end_button()
    {
        WpfHost.Run(() =>
        {
            var (window, view, _) = Open(null);
            Assert.Equal(["Пополнить баланс", "Вызвать администратора"], RightButtons(view).Select(b => b.Text));
            Assert.Contains(Descendants<TextBlock>(view), t => t.Text == "Сессия не начата" && t.IsVisible);
            Assert.DoesNotContain(Descendants<TextBlock>(view), t => t.Text == "Осталось" && t.IsVisible);
            window.Close();
        });
    }

    [Fact]
    public void On_the_bar_tab_there_is_only_one_white_filled_button_and_extension_becomes_an_outline()
    {
        WpfHost.Run(() =>
        {
            var (window, view, _) = Open("package", page: LauncherPage.Bar);
            var primary = (Style)Application.Current.Resources["Primary"];
            var outline = (Style)Application.Current.Resources["ActionTileAccent"];
            var buttons = RightButtons(view);
            Assert.Equal(["Заказать", "Продлить тариф", "Пополнить баланс", "Вызвать администратора", "Завершить сессию"], buttons.Where(b => b.Text is not ("Убрать" or "Добавить")).Select(b => b.Text));
            Assert.Equal(["Заказать"], buttons.Where(b => ReferenceEquals(b.Button.Style, primary)).Select(b => b.Text));
            Assert.True(ReferenceEquals(buttons.Single(b => b.Text == "Продлить тариф").Button.Style, outline));
            window.Close();
        });
    }

    [Fact]
    public void On_other_tabs_the_extend_button_is_the_white_filled_one()
    {
        WpfHost.Run(() =>
        {
            var (window, view, _) = Open("package");
            var primary = (Style)Application.Current.Resources["ActionTilePrimary"];
            Assert.True(ReferenceEquals(RightButtons(view).Single(b => b.Text == "Продлить тариф").Button.Style, primary));
            window.Close();
        });
    }

    [Fact]
    public void The_navigation_shows_extension_during_a_package_session_and_the_extension_cards_list_packages_only()
    {
        WpfHost.Run(() =>
        {
            var (window, view, _) = Open("package", page: LauncherPage.Tariffs);
            Assert.Contains(Descendants<RadioButton>(view), r => r.IsVisible && r.Content as string == "Продление");
            var cards = Descendants<Button>(view).Where(b => b.IsVisible && b.DataContext is TariffView).Select(b => ((TariffView)b.DataContext).Name).ToList();
            Assert.Equal(["3 часа"], cards);                     // почасового тарифа среди карточек продления нет
            window.Close();
        });
    }

    [Fact]
    public void The_session_status_is_a_round_dot_with_its_label_at_the_very_bottom_of_the_right_column()
    {
        WpfHost.Run(() =>
        {
            var (window, view, _) = Open("package");
            var column = view.ActualWidth - 340;
            var status = Descendants<TextBlock>(view).Single(t => t.Text == "Идёт сессия" && t.IsVisible);
            var lastButton = RightButtons(view).Last();
            var box = Bounds(status, view);

            Assert.True(box.Left >= column, "подпись в правой колонке");
            Assert.True(box.Top > lastButton.Box.Bottom, "статус ниже всех кнопок");
            Assert.True(view.ActualHeight - box.Bottom < 60, "и прижат к низу колонки");
            var dots = Descendants<Ellipse>(view).Where(e => e.IsVisible && Bounds(e, view).Left >= column && Bounds(e, view).Top > lastButton.Box.Bottom).ToList();
            Assert.Equal(2, dots.Count);                                                           // круг и его ореол
            Assert.All(dots, d => Assert.Equal(d.ActualWidth, d.ActualHeight));
            Assert.DoesNotContain(Descendants<Rectangle>(view), r => r.IsVisible && r.ActualWidth == 10 && r.ActualHeight == 10);   // квадратика больше нет
            window.Close();
        });
    }

    [Fact]
    public void Before_a_session_the_bottom_status_says_it_has_not_started()
    {
        WpfHost.Run(() =>
        {
            var (window, view, _) = Open(null);
            var status = Descendants<TextBlock>(view).Single(t => t.Text == "Сессия не начата" && t.IsVisible);
            Assert.True(view.ActualHeight - Bounds(status, view).Bottom < 90);
            Assert.DoesNotContain(Descendants<TextBlock>(view), t => t.Text == "Идёт сессия" && t.IsVisible);
            window.Close();
        });
    }

    [Fact]
    public void The_status_dot_turns_orange_when_the_tariff_has_ended()
    {
        WpfHost.Run(() =>
        {
            var (window, view, _) = Open("package", overtime: true);
            var warn = ((System.Windows.Media.SolidColorBrush)Application.Current.Resources["Warn"]).Color;
            var text = Descendants<TextBlock>(view).Single(t => t.Text == "Тариф закончился" && t.IsVisible);
            Assert.Equal(warn, ((System.Windows.Media.SolidColorBrush)text.Foreground).Color);
            var dot = Descendants<Ellipse>(view).First(e => e.IsVisible && e.ActualWidth == 10);
            Assert.Equal(warn, ((System.Windows.Media.SolidColorBrush)dot.Fill).Color);
            window.Close();
        });
    }

    [Fact]
    public void The_status_halo_has_room_to_spread_so_it_is_never_clipped_into_a_square()
    {
        WpfHost.Run(() =>
        {
            var (window, view, _) = Open("package");
            var halo = Descendants<Ellipse>(view).First(e => e.IsVisible && e.Style == (Style)Application.Current.Resources["StatusHalo"]);
            var host = (FrameworkElement)halo.Parent;
            Assert.True(host.ActualWidth >= 30 && host.ActualHeight >= 30, $"поле под волну {host.ActualWidth}x{host.ActualHeight}: волна растёт до 30 px и обрезалась бы по краю поля");
            Assert.False(host.ClipToBounds);
            window.Close();
        });
    }
}
