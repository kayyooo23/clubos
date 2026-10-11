using System.Windows;
using System.Windows.Controls;
using ClubOS.Overlay;
using ClubOS.Shared;
using ClubOS.Shared.Ui;
using Microsoft.Extensions.Time.Testing;

namespace ClubOS.Tests;

/// <summary>
/// Раскладка экрана блокировки: заставка с баннером на весь экран, а после нажатия форма входа справа и название клуба, статус и номер ПК
/// в левом верхнем углу. Проверяем настоящее окно: положение и размеры элементов после раскладки.
/// </summary>
public sealed class LockLayoutTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clubos-layout-" + Guid.NewGuid().ToString("N"));

    public LockLayoutTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* временная папка */ }
    }

    private sealed class NoGames : IAppLauncher
    {
        public bool IsAvailable(AppInfo app) => true;
        public string? Launch(AppInfo app) => null;
    }

    private sealed record Rig(LockWindow Window, LauncherModel Model, FlowController Flow, FakeAgent Agent, FakeTimeProvider Time);

    private static void Pump(Window window) =>
        window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => { }));

    /// <summary>Показывает окно за краем экрана нужного размера. ads: баннеры, которые «пришли от службы» (файлы лежат в _dir).</summary>
    private Rig Open(double width, double height, IReadOnlyList<AdInfo> ads)
    {
        var agent = new FakeAgent();
        agent.Handler = r => r.Op == Ops.Ads
            ? new IpcResponse(r.Id, true, 200, null, AgentSnapshot.Booting, Ads: ads)
            : agent.Ok(r, tariffs: [], apps: []);
        agent.Current = new AgentSnapshot(true, PcMode.Locked, null, null, null, null, "Demo Club", "PC-1");
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        var flow = new FlowController(agent, time);
        var model = new LauncherModel(flow, agent, new NoGames(), time, _dir);
        flow.PollAsync().GetAwaiter().GetResult();
        var window = new LockWindow(flow, model, kiosk: false)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
            Width = width, Height = height,
        };
        window.Show();
        Pump(window);
        model.PollAsync().GetAwaiter().GetResult();
        Pump(window);
        return new Rig(window, model, flow, agent, time);
    }

    private AdInfo Ad(int id)
    {
        var path = Path.Combine(_dir, $"{id}-{id:x8}.png");
        WriteBanner(path);
        return new AdInfo(id, "ООО Ромашка", "abc", 10, 1920, 320, $"{id:x8}", path);
    }

    private static void WriteBanner(string path)
    {
        var pixels = new byte[16 * 16 * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 40; pixels[i + 1] = 60; pixels[i + 2] = 200; pixels[i + 3] = 255; }
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(System.Windows.Media.Imaging.BitmapSource.Create(
            16, 16, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, 16 * 4)));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    /// <summary>Прямоугольник элемента в координатах содержимого окна.</summary>
    private static Rect Bounds(FrameworkElement element, FrameworkElement root) =>
        element.TransformToAncestor(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));   // с учётом Viewbox: уменьшенная карточка меряется как есть

    private static IEnumerable<T> Descendants<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    // ---------- заставка ----------
    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1366, 768)]
    [InlineData(1024, 640)]
    public void With_ads_an_idle_pc_shows_the_banner_over_the_whole_window_with_the_caption_and_the_carousel_dots(double width, double height)
    {
        WpfHost.Run(() =>
        {
            var rig = Open(width, height, [Ad(1), Ad(2)]);
            var window = rig.Window;
            var root = (FrameworkElement)window.Content;
            var attract = (AttractView)window.FindName("Attract");

            Assert.True(rig.Model.Attract.IsAttract);
            Assert.Equal(Visibility.Visible, attract.Visibility);
            var screen = Bounds(attract, root);
            Assert.Equal((0, 0), (screen.X, screen.Y));
            Assert.Equal(root.ActualWidth, screen.Width, 1);                       // на весь экран, а не полоса
            Assert.Equal(root.ActualHeight, screen.Height, 1);
            Assert.Equal(1, attract.Shown!.Id);
            Assert.Equal(2, attract.DotCount);                                      // точки карусели: по одной на баннер

            var caption = Bounds((FrameworkElement)attract.FindName("Caption"), root);   // «РЕКЛАМА · рекламодатель · erid» на самом баннере, слева внизу
            Assert.InRange(caption.Left, 40, 56);
            Assert.InRange(root.ActualHeight - caption.Bottom, 32, 48);
            Assert.True(caption.Height < 60 && caption.Width < root.ActualWidth / 2);
            window.AllowClose = true;
            window.Close();
        });
    }

    [Fact]
    public void A_click_replaces_the_screensaver_with_the_form_and_the_form_is_laid_out_as_before()
    {
        WpfHost.Run(() =>
        {
            var rig = Open(1920, 1080, [Ad(1)]);
            var window = rig.Window;
            var root = (FrameworkElement)window.Content;
            var attract = (AttractView)window.FindName("Attract");
            Assert.Equal(Visibility.Visible, attract.Visibility);

            Assert.True(rig.Model.Attract.Input(InputKind.Click));
            Pump(window);
            Assert.Equal(Visibility.Collapsed, attract.Visibility);

            var login = Bounds((FrameworkElement)window.FindName("LoginPanel"), root);
            Assert.Equal(484, login.Width, 1);                                     // на большом экране карточка в натуральную величину
            Assert.InRange(login.Left + login.Width / 2, root.ActualWidth / 2 - 1, root.ActualWidth / 2 + 1);   // по центру окна по ширине
            Assert.InRange(login.Top + login.Height / 2, root.ActualHeight / 2 - 1, root.ActualHeight / 2 + 1);  // и по высоте
            Assert.True(login.Height < root.ActualHeight - 100, "это отдельная рамка, а не панель на всю высоту");
            var who = Bounds((FrameworkElement)window.FindName("Identity"), root);
            Assert.True(who.Left < 100 && who.Top < 100, $"название клуба, статус и номер ПК должны быть в левом верхнем углу, а они в {who.TopLeft}");
            window.AllowClose = true;
            window.Close();
        });
    }

    [Fact]
    public void Without_ads_there_is_no_screensaver_and_the_form_is_on_screen_at_once()
    {
        WpfHost.Run(() =>
        {
            var rig = Open(1366, 768, []);
            var window = rig.Window;
            var root = (FrameworkElement)window.Content;
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("Attract")).Visibility);
            var login = Bounds((FrameworkElement)window.FindName("LoginPanel"), root);
            Assert.InRange(login.Left + login.Width / 2, root.ActualWidth / 2 - 1, root.ActualWidth / 2 + 1);
            Assert.InRange(login.Top + login.Height / 2, root.ActualHeight / 2 - 1, root.ActualHeight / 2 + 1);
            window.AllowClose = true;
            window.Close();
        });
    }

    [Fact]
    public void The_screensaver_is_not_shown_once_the_guest_is_logged_in()
    {
        WpfHost.Run(() =>
        {
            var rig = Open(1366, 768, [Ad(1)]);
            var window = rig.Window;
            var attract = (FrameworkElement)window.FindName("Attract");
            Assert.Equal(Visibility.Visible, attract.Visibility);

            rig.Agent.Current = new AgentSnapshot(true, PcMode.Locked, new AccountInfo(2, "guest", "+7 *** ***-00-01", 500), null, null, null);
            rig.Flow.PollAsync().GetAwaiter().GetResult();
            rig.Model.PollAsync().GetAwaiter().GetResult();
            Pump(window);
            Assert.Equal(Visibility.Collapsed, attract.Visibility);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("AuthGrid")).Visibility);   // вместо формы входа меню гостя
            window.AllowClose = true;
            window.Close();
        });
    }

    // ---------- форма на маленьком экране ----------
    [Theory]
    [InlineData(Screen.Login)]
    [InlineData(Screen.RegisterPhone)]
    [InlineData(Screen.RegisterConfirm)]
    [InlineData(Screen.ResetPhone)]
    [InlineData(Screen.ResetConfirm)]
    public void Every_form_fits_into_the_centered_login_card_even_on_a_small_screen(Screen screen)
    {
        // Самая длинная форма (регистрация: четыре поля, согласие, кнопка) на экране 1366 x 768: кнопки не должны уйти за край панели.
        WpfHost.Run(() =>
        {
            var rig = Open(1366, 768, []);
            var window = rig.Window;
            rig.Flow.GoTo(screen);
            Pump(window);
            var root = (FrameworkElement)window.Content;
            var panel = (FrameworkElement)window.FindName("LoginPanel");
            var area = Bounds(panel, root);
            Assert.True(area.Top >= -0.5 && area.Bottom <= root.ActualHeight + 0.5, $"карточка ({area.Top}..{area.Bottom}) вышла за окно высотой {root.ActualHeight} на экране {screen}");
            var buttons = Descendants<Button>(panel).Where(b => b.IsVisible).ToList();
            Assert.NotEmpty(buttons);
            foreach (var button in buttons)
            {
                var b = Bounds(button, root);
                Assert.True(b.Top >= area.Top - 0.5 && b.Bottom <= area.Bottom + 0.5, $"кнопка «{button.Content}» ({b.Top}..{b.Bottom}) вне панели ({area.Top}..{area.Bottom}) на экране {screen}");
            }
            window.AllowClose = true;
            window.Close();
        });
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1366, 768)]
    [InlineData(1280, 820)]
    public void The_corner_label_never_runs_under_the_centered_login_card(int width, int height)
    {
        WpfHost.Run(() =>
        {
            var rig = Open(width, height, []);
            var window = rig.Window;
            window.UpdateLayout();
            Pump(window);
            var root = (FrameworkElement)window.Content;
            var name = Bounds((FrameworkElement)window.FindName("Identity"), root);
            var card = Bounds((FrameworkElement)window.FindName("LoginPanel"), root);
            Assert.True(name.Right <= card.Left - 8, $"метка слева сверху (правый край {name.Right}) заходит под карточку входа (левый край {card.Left}) на экране {width}x{height}");
            window.AllowClose = true;
            window.Close();
        });
    }
}
