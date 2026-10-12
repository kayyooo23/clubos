using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClubOS.Overlay;
using ClubOS.Service;
using ClubOS.Shared;
using ClubOS.Shared.Ui;
using Microsoft.Extensions.Time.Testing;

namespace ClubOS.Tests;

/// <summary>Обложки игр на ПК: служба скачивает и проверяет, модель принимает только свои файлы, меню рисует постеры на сетке во всю ширину.</summary>
public sealed class CoverClientTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clubos-covers-" + Guid.NewGuid().ToString("N"));

    public CoverClientTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* временная папка */ }
    }

    private static byte[] PngBytes(int seed)
    {
        var bytes = new byte[600];
        new Random(seed).NextBytes(bytes);
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        return bytes;
    }

    private static byte[] JpegBytes(int seed)
    {
        var bytes = new byte[600];
        new Random(seed).NextBytes(bytes);
        new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }.CopyTo(bytes, 0);
        return bytes;
    }

    private static string VersionOf(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant()[..16];

    private static string AppsJson(params (int Id, string Title, string? Version)[] apps) =>
        "[" + string.Join(",", apps.Select(a =>
            $$"""{"id":{{a.Id}},"title":"{{a.Title}}","tab":"games","category":"Шутеры","age_rating":16,"launch_kind":"steam","launch_target":"{{a.Id}}","cover_version":{{(a.Version is null ? "null" : $"\"{a.Version}\"")}}}""")) + "]";

    // ---------- служба ----------
    [Fact]
    public async Task The_service_downloads_a_cover_once_checks_it_and_gives_the_overlay_a_local_file()
    {
        var (core, server, _) = Make.Core(coversDir: _dir);
        var png = PngBytes(1);
        server.On("GET", "/api/agent/apps", 200, AppsJson((1, "Dota 2", VersionOf(png)), (2, "Без обложки", null)))
              .OnBytes("GET", "/api/agent/apps/1/cover", 200, png);
        var resp = await core.HandleAsync(new IpcRequest(1, Ops.Apps));
        Assert.True(resp.Ok);
        Assert.Equal(Path.Combine(_dir, $"1-{VersionOf(png)}.png"), resp.Apps![0].CoverPath);
        Assert.Equal(png, File.ReadAllBytes(resp.Apps[0].CoverPath!));
        Assert.Null(resp.Apps[1].CoverPath);                                          // у второй игры обложки нет
        var seen = server.To("/api/agent/apps/1/cover").Single();
        Assert.Equal(FakeServer.PcKey, seen.PcKey);
        Assert.Null(seen.Auth);

        await core.HandleAsync(new IpcRequest(2, Ops.Apps));
        Assert.Single(server.To("/api/agent/apps/1/cover"));                           // файл уже лежит: второй раз не качаем
    }

    [Fact]
    public async Task A_replaced_cover_is_downloaded_again_the_old_file_and_removed_games_leave_nothing_behind_but_foreign_files_stay()
    {
        var (core, server, _) = Make.Core(coversDir: _dir);
        var first = PngBytes(1);
        var second = JpegBytes(2);
        var other = PngBytes(3);
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "не наше");
        server.On("GET", "/api/agent/apps", 200, AppsJson((1, "A", VersionOf(first)), (2, "B", VersionOf(other))))
              .OnBytes("GET", "/api/agent/apps/1/cover", 200, first).OnBytes("GET", "/api/agent/apps/2/cover", 200, other);
        var one = await core.HandleAsync(new IpcRequest(1, Ops.Apps));
        Assert.Equal(2, Directory.EnumerateFiles(_dir, "*.png").Count());

        server.On("GET", "/api/agent/apps", 200, AppsJson((1, "A", VersionOf(second))))      // у первой сменилась обложка, вторую убрали из меню
              .OnBytes("GET", "/api/agent/apps/1/cover", 200, second);
        var two = await core.HandleAsync(new IpcRequest(2, Ops.Apps));
        Assert.EndsWith(".jpg", two.Apps![0].CoverPath);                                      // тип определяется по самим байтам
        Assert.True(File.Exists(two.Apps[0].CoverPath));
        Assert.False(File.Exists(one.Apps![0].CoverPath));                                    // прежняя версия удалена
        Assert.False(File.Exists(one.Apps[1].CoverPath));                                     // убранная игра тоже
        Assert.True(File.Exists(Path.Combine(_dir, "notes.txt")));
    }

    [Fact]
    public async Task A_cover_with_a_wrong_fingerprint_type_size_or_an_unsafe_version_is_not_used()
    {
        var (core, server, _) = Make.Core(coversDir: _dir);
        var good = PngBytes(1);
        var gif = "GIF89a"u8.ToArray().Concat(new byte[500]).ToArray();
        var huge = PngBytes(3).Concat(new byte[CoverCache.MaxBytes]).ToArray();
        server.On("GET", "/api/agent/apps", 200, AppsJson(
                (1, "Подмена", VersionOf(good)),                 // отпечаток не тот: сервер прислал не то, что обещал
                (2, "Гиф", VersionOf(gif)), (3, "Огромная", VersionOf(huge)),
                (4, "Плохая версия", @"..\\..\\evil"), (5, "Заглавные", "ABCDEF1234567890")))
              .OnBytes("GET", "/api/agent/apps/1/cover", 200, PngBytes(99)).OnBytes("GET", "/api/agent/apps/2/cover", 200, gif)
              .OnBytes("GET", "/api/agent/apps/3/cover", 200, huge).OnBytes("GET", "/api/agent/apps/4/cover", 200, good).OnBytes("GET", "/api/agent/apps/5/cover", 200, good);
        var resp = await core.HandleAsync(new IpcRequest(1, Ops.Apps));
        Assert.True(resp.Ok);
        Assert.Equal(5, resp.Apps!.Count);
        Assert.All(resp.Apps, a => Assert.Null(a.CoverPath));
        Assert.Empty(Directory.EnumerateFiles(_dir));
        Assert.DoesNotContain(server.Requests, r => r.Path.EndsWith("/4/cover") || r.Path.EndsWith("/5/cover"));   // по небезопасной версии в сеть даже не ходим
    }

    [Fact]
    public async Task One_cover_that_fails_to_download_does_not_hide_the_others_or_the_game_itself()
    {
        var (core, server, _) = Make.Core(coversDir: _dir);
        var a = PngBytes(1);
        var b = PngBytes(2);
        server.On("GET", "/api/agent/apps", 200, AppsJson((1, "A", VersionOf(a)), (2, "B", VersionOf(b))))
              .OnBytes("GET", "/api/agent/apps/1/cover", 200, a).OnBytes("GET", "/api/agent/apps/2/cover", 500, "{}"u8.ToArray());
        var resp = await core.HandleAsync(new IpcRequest(1, Ops.Apps));
        Assert.Equal(["A", "B"], resp.Apps!.Select(x => x.Title));                            // обе игры в списке
        Assert.NotNull(resp.Apps![0].CoverPath);
        Assert.Null(resp.Apps![1].CoverPath);                                                  // меню спросит ещё раз через несколько секунд
    }

    [Fact]
    public async Task Downloading_covers_does_not_hold_up_the_guest_login()
    {
        var (core, server, _) = Make.Core(coversDir: _dir);
        var png = PngBytes(1);
        var release = new TaskCompletionSource();
        server.On("GET", "/api/agent/state", 200, Samples.Cabinet).On("POST", "/api/auth/login", 200, Samples.LoginOk)
              .On("GET", "/api/agent/apps", 200, AppsJson((1, "A", VersionOf(png))))
              .OnBytes("GET", "/api/agent/apps/1/cover", _ => { release.Task.GetAwaiter().GetResult(); return (200, png); });
        var apps = Task.Run(() => core.HandleAsync(new IpcRequest(1, Ops.Apps)));
        await Task.Delay(100);                                                                // обложка «качается»
        var login = await core.HandleAsync(new IpcRequest(2, Ops.Login, Phone: "+79000000001", Password: "p")).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(login.Ok);
        release.SetResult();
        Assert.True((await apps).Ok);
    }

    // ---------- модель меню ----------
    private sealed class NoGames : IAppLauncher
    {
        public bool IsAvailable(AppInfo app) => true;
        public string? Launch(AppInfo app) => null;
    }

    private (LauncherModel Model, FlowController Flow, FakeAgent Agent, FakeTimeProvider Time) Menu(Func<IReadOnlyList<AppInfo>> apps)
    {
        var agent = new FakeAgent();
        agent.Handler = r => r.Op == Ops.Apps ? agent.Ok(r, apps: apps()) : agent.Ok(r, tariffs: []);
        agent.Current = new AgentSnapshot(true, PcMode.GuestSession, new AccountInfo(2, "guest", "+7 *** ***-00-01", 250),
            new SessionInfo(1, 1, 2, "3 часа", DateTime.UtcNow, null, null, 250, 3600, Kind: "package"), null, 3600);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        var flow = new FlowController(agent, time);
        return (new LauncherModel(flow, agent, new NoGames(), time, coversDir: _dir), flow, agent, time);
    }

    [Fact]
    public async Task A_card_takes_a_cover_only_from_the_covers_folder_and_only_png_or_jpg()
    {
        var inside = Path.Combine(_dir, "1-aaaaaaaa.png");
        var outside = Path.Combine(Path.GetTempPath(), "evil.png");
        var climbing = Path.Combine(_dir, "..", "x.png");
        var wrongType = Path.Combine(_dir, "4-bbbbbbbb.exe");
        var apps = new AppInfo[]
        {
            new(1, "Внутри", "games", "A", null, "steam", "1", "aaaaaaaa", inside), new(2, "Снаружи", "games", "A", null, "steam", "2", "aaaaaaab", outside),
            new(3, "Вверх", "games", "A", null, "steam", "3", "aaaaaaac", climbing), new(4, "Не картинка", "games", "A", null, "steam", "4", "bbbbbbbb", wrongType),
            new(5, "Без обложки", "games", "A", null, "steam", "5"),
        };
        var (model, flow, _, _) = Menu(() => apps);
        await flow.PollAsync();
        await model.PollAsync();
        Assert.Equal([inside, null, null, null, null], model.GameCards.Select(c => c.CoverPath));
        Assert.Equal([true, false, false, false, false], model.GameCards.Select(c => c.HasCover));
    }

    [Fact]
    public async Task While_covers_are_still_downloading_the_list_is_asked_again_in_a_few_seconds_and_afterwards_rarely()
    {
        var path = Path.Combine(_dir, "1-aaaaaaaa.png");
        var ready = false;
        var (model, flow, agent, time) = Menu(() => [new AppInfo(1, "Игра", "games", "A", null, "steam", "1", "aaaaaaaa", ready ? path : null)]);
        await flow.PollAsync();
        await model.PollAsync();
        Assert.Null(model.GameCards[0].CoverPath);
        Assert.Single(agent.Ops(Ops.Apps));

        time.Advance(TimeSpan.FromSeconds(3));
        await flow.PollAsync(); await model.PollAsync();
        Assert.Single(agent.Ops(Ops.Apps));                                                   // рано
        ready = true;
        time.Advance(TimeSpan.FromSeconds(3));
        await flow.PollAsync(); await model.PollAsync();
        Assert.Equal(2, agent.Ops(Ops.Apps).Count());
        Assert.Equal(path, model.GameCards[0].CoverPath);                                      // плитка получила обложку

        time.Advance(TimeSpan.FromSeconds(30));
        await flow.PollAsync(); await model.PollAsync();
        Assert.Equal(2, agent.Ops(Ops.Apps).Count());                                          // дальше обычные 5 минут
    }

    [Fact]
    public async Task Steam_games_without_posters_make_the_menu_ask_again_soon_a_few_times_and_then_rarely()
    {
        // сервер подтягивает постеры Steam сам, уже после ответа: первые ответы приходят без обложек, через несколько секунд они появляются
        var (model, flow, agent, time) = Menu(() => [new AppInfo(1, "Игра", "games", "A", null, "steam", "1")]);
        await flow.PollAsync();
        await model.PollAsync();
        Assert.Single(agent.Ops(Ops.Apps));
        for (var i = 0; i < 6; i++)
        {
            time.Advance(TimeSpan.FromSeconds(11));
            await flow.PollAsync(); await model.PollAsync();
        }
        Assert.Equal(7, agent.Ops(Ops.Apps).Count());                                          // шесть быстрых повторов через 10 секунд
        time.Advance(TimeSpan.FromSeconds(11));
        await flow.PollAsync(); await model.PollAsync();
        Assert.Equal(7, agent.Ops(Ops.Apps).Count());                                          // постера в Steam, видимо, нет: дальше обычные 5 минут
        time.Advance(TimeSpan.FromMinutes(5));
        await flow.PollAsync(); await model.PollAsync();
        Assert.Equal(8, agent.Ops(Ops.Apps).Count());
    }

    [Fact]
    public async Task A_poster_that_appears_stops_the_quick_asking()
    {
        var path = Path.Combine(_dir, "1-aaaaaaaa.png");
        var ready = false;
        var (model, flow, agent, time) = Menu(() => [new AppInfo(1, "Игра", "games", "A", null, "steam", "1", ready ? "aaaaaaaa" : null, ready ? path : null)]);
        await flow.PollAsync();
        await model.PollAsync();
        Assert.Null(model.GameCards[0].CoverPath);
        ready = true;
        time.Advance(TimeSpan.FromSeconds(11));
        await flow.PollAsync(); await model.PollAsync();
        Assert.Equal(2, agent.Ops(Ops.Apps).Count());
        Assert.Equal(path, model.GameCards[0].CoverPath);                                      // постер появился
        time.Advance(TimeSpan.FromSeconds(30));
        await flow.PollAsync(); await model.PollAsync();
        Assert.Equal(2, agent.Ops(Ops.Apps).Count());                                          // больше не спрашиваем до обычных 5 минут
    }

    [Fact]
    public async Task Programs_and_non_steam_games_without_a_cover_do_not_cause_quick_asking()
    {
        var (model, flow, agent, time) = Menu(() => [new AppInfo(1, "Chrome", "apps", "Браузеры", null, "exe", @"C:\chrome.exe"),
            new AppInfo(2, "Игра", "games", "A", null, "exe", @"C:\game.exe")]);
        await flow.PollAsync();
        await model.PollAsync();
        time.Advance(TimeSpan.FromSeconds(30));
        await flow.PollAsync(); await model.PollAsync();
        Assert.Single(agent.Ops(Ops.Apps));
    }

    // ---------- сетка ----------
    [Fact]
    public void The_grid_fills_the_whole_width_with_as_many_posters_as_fit()
    {
        WpfHost.Run(() =>
        {
            var panel = new UniformWrapPanel { MinItemWidth = 170, Gap = 16, HeightPerWidth = 1.5, ExtraHeight = 48 };
            var (columns, item) = panel.Layout(1316);
            Assert.Equal(7, columns);
            Assert.True(columns * item.Width + (columns - 1) * 16 <= 1316 && columns * item.Width + (columns - 1) * 16 > 1316 - columns);   // без пустой полосы справа
            Assert.Equal(Math.Round(item.Width * 1.5 + 48), item.Height);
            Assert.Equal(1, panel.Layout(300).Columns);                                        // узкое окно: одна плитка в ряд
            Assert.Equal(2, panel.Layout(400).Columns);
            var programs = new UniformWrapPanel { MinItemWidth = 240, FixedHeight = 64 };
            Assert.Equal(64, programs.Layout(1316).Item.Height);
        });
    }

    // ---------- окно ----------
    private static void WritePng(string path)
    {
        var pixels = new byte[8 * 12 * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 180; pixels[i + 1] = 80; pixels[i + 2] = 40; pixels[i + 3] = 255; }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(8, 12, 96, 96, PixelFormats.Bgra32, null, pixels, 8 * 4)));
        using var file = File.Create(path);
        encoder.Save(file);
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

    [Fact]
    public void A_game_with_a_cover_shows_the_picture_and_one_without_shows_the_letter_and_the_grid_has_no_ragged_edge()
    {
        var withCover = Path.Combine(_dir, "1-aaaaaaaa.png");
        WritePng(withCover);
        var apps = Enumerable.Range(1, 15).Select(i => new AppInfo(i, i == 1 ? "Постер" : $"Игра {i}", "games", "Шутеры", 16, "steam", i.ToString(), i == 1 ? "aaaaaaaa" : null, i == 1 ? withCover : null)).ToList();
        WpfHost.Run(() =>
        {
            var (model, flow, _, _) = Menu(() => apps);
            flow.PollAsync().GetAwaiter().GetResult();
            model.PollAsync().GetAwaiter().GetResult();
            var view = new LauncherView { DataContext = model };
            var window = new Window { Content = view, Width = 1920, Height = 1080, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            window.Show();
            window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => { }));
            view.UpdateLayout();

            var tiles = Descendants<Button>(view).Where(b => b.IsVisible && b.DataContext is AppCard).ToList();
            Assert.Equal(15, tiles.Count);
            var first = tiles.First(t => ((AppCard)t.DataContext).Title == "Постер");
            var plain = tiles.First(t => ((AppCard)t.DataContext).Title == "Игра 2");
            first.ApplyTemplate();
            var cover = (Image)first.Template.FindName("Cover", first);
            Assert.NotNull(cover.Source);                                                      // постер нарисован
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)first.Template.FindName("Placeholder", first)).Visibility);
            Assert.Null(((Image)plain.Template.FindName("Cover", plain)).Source);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)plain.Template.FindName("Placeholder", plain)).Visibility);   // у остальных плитка с буквой

            // сетка: плитки первого ряда занимают всю ширину раздела, справа нет пустой полосы
            var rowTop = tiles.Min(t => t.TranslatePoint(new Point(0, 0), view).Y);
            var row = tiles.Where(t => Math.Abs(t.TranslatePoint(new Point(0, 0), view).Y - rowTop) < 1).ToList();
            var panel = Descendants<UniformWrapPanel>(view).First(p => p.Children.Count == 15);
            var right = row.Max(t => t.TranslatePoint(new Point(t.ActualWidth, 0), view).X);
            var panelRight = panel.TranslatePoint(new Point(panel.ActualWidth, 0), view).X;
            Assert.Equal(panel.Layout(panel.ActualWidth).Columns, row.Count);                  // в ряду столько плиток, сколько посчитала сетка
            Assert.True(row.Count >= 6, "на экране 1920 в ряду не меньше шести постеров");
            Assert.InRange(panelRight - right, 0, 8);
            Assert.All(row, t => Assert.Equal(row[0].ActualWidth, t.ActualWidth, 1));
            Assert.InRange(Math.Abs(row[0].ActualWidth * 1.5 + 48 - row[0].ActualHeight), 0, 1);   // постер 2 на 3 плюс строка названия; допуск в пиксель: при масштабе экрана 125% размеры округляются до его пикселей
            window.Close();
        });
    }

    [Fact]
    public void A_game_that_is_not_installed_is_dimmed_and_programs_are_compact_tiles()
    {
        WpfHost.Run(() =>
        {
            var apps = new AppInfo[]
            {
                new(1, "Игра", "games", "A", 12, "steam", "1"),
                new(2, "Chrome", "apps", "Браузеры", null, "exe", @"C:\chrome.exe"),
            };
            var (model, flow, agent, _) = Menu(() => apps);
            var view = new LauncherView { DataContext = model };
            flow.PollAsync().GetAwaiter().GetResult();
            model.PollAsync().GetAwaiter().GetResult();
            var window = new Window { Content = view, Width = 1920, Height = 1080, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            window.Show();
            window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => { }));
            view.UpdateLayout();
            var program = Descendants<Button>(view).Single(b => b.IsVisible && b.DataContext is AppCard c && c.Title == "Chrome");
            Assert.Equal(64, program.ActualHeight);
            Assert.True(program.ActualWidth >= 240);
            window.Close();
        });
    }
}
