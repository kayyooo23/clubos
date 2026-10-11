using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClubOS.Overlay;
using ClubOS.Service;
using ClubOS.Shared;
using ClubOS.Shared.Ui;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ClubOS.Tests;

/// <summary>Иконки программ: достаются из самого exe, уходят на сервер вместе со списком и видны на плитке программы в меню гостя.</summary>
public sealed class ProgramIconTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clubos-test-icons-" + Guid.NewGuid().ToString("N"));

    public ProgramIconTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* временная папка */ }
    }

    private static BitmapSource Decode(byte[] png)
    {
        var decoder = new PngBitmapDecoder(new MemoryStream(png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return decoder.Frames[0];
    }

    private static byte[] Pixel(BitmapSource image, int x, int y)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var one = new byte[4];
        converted.CopyPixels(new Int32Rect(x, y, 1, 1), one, 4, 0);
        return one;                                                              // синий, зелёный, красный, прозрачность
    }

    // ---------- кодировщик PNG ----------
    [Fact]
    public void The_encoder_makes_a_valid_png_with_the_right_size_and_colours()
    {
        var bgra = new byte[]
        {
            0, 0, 255, 255,       10, 200, 30, 255,        // красный, зелёный
            255, 0, 0, 255,       0, 0, 0, 0,              // синий, полностью прозрачный
        };
        var png = PngEncoder.Encode(2, 2, bgra, premultiplied: false);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png.Take(8));
        var image = Decode(png);
        Assert.Equal((2, 2), (image.PixelWidth, image.PixelHeight));
        Assert.Equal([0, 0, 255, 255], Pixel(image, 0, 0));
        Assert.Equal([10, 200, 30, 255], Pixel(image, 1, 0));
        Assert.Equal([255, 0, 0, 255], Pixel(image, 0, 1));
        Assert.Equal(0, Pixel(image, 1, 1)[3]);
    }

    [Fact]
    public void Premultiplied_pixels_are_converted_back_so_semi_transparent_edges_keep_their_colour()
    {
        // красный цвет при прозрачности 50% хранится умноженным: 127 вместо 255
        var png = PngEncoder.Encode(1, 1, [0, 0, 127, 127], premultiplied: true);
        var pixel = Pixel(Decode(png), 0, 0);
        Assert.InRange(pixel[2], 253, 255);
        Assert.Equal(127, pixel[3]);
    }

    // ---------- настоящая иконка Windows ----------
    [Fact]
    public void A_real_windows_program_gives_a_png_icon_of_the_expected_size()
    {
        var notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        Assert.True(File.Exists(notepad));
        var icons = new ShellProgramIcons().For([notepad]);
        Assert.True(icons.TryGetValue(notepad, out var png), "иконка блокнота не получена");
        Assert.InRange(png!.Length, 200, ShellProgramIcons.MaxBytes);
        var image = Decode(png);
        Assert.InRange(image.PixelWidth, 48, 96);
        Assert.Equal(image.PixelWidth, image.PixelHeight);
        var any = false;
        for (var y = 0; y < image.PixelHeight && !any; y += 4)
            for (var x = 0; x < image.PixelWidth && !any; x += 4) any = Pixel(image, x, y)[3] > 0;
        Assert.True(any, "на иконке нет ни одного видимого пикселя");
    }

    [Fact]
    public void A_missing_file_or_a_non_program_simply_has_no_icon_and_does_not_break_the_others()
    {
        var notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        var icons = new ShellProgramIcons().For([Path.Combine(_dir, "no-such-file.exe"), notepad]);
        Assert.False(icons.ContainsKey(Path.Combine(_dir, "no-such-file.exe")));
        Assert.True(icons.ContainsKey(notepad));
    }

    // ---------- список с иконками ----------
    private sealed class FakeIcons(Dictionary<string, byte[]> map) : IProgramIcons
    {
        public IReadOnlyDictionary<string, byte[]> For(IReadOnlyList<string> exePaths) => map;
    }

    private sealed class Links : IShortcutResolver
    {
        public Dictionary<string, string> Map { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? Target(string shortcutPath) => Map.TryGetValue(shortcutPath, out var t) ? t : null;
    }

    private string Lnk(Links links, string name, string target)
    {
        var path = Path.Combine(_dir, name + ".lnk");
        File.WriteAllText(path, "");
        links.Map[path] = target;
        return path;
    }

    [Fact]
    public void The_scanner_attaches_the_icon_to_the_matching_program_and_leaves_others_without()
    {
        var links = new Links();
        Lnk(links, "TeamSpeak", @"C:\Apps\TS\ts.exe");
        Lnk(links, "Plain", @"C:\Apps\Plain\p.exe");
        var icons = new FakeIcons(new() { [@"C:\Apps\TS\ts.exe"] = [1, 2, 3] });
        var found = InstalledPrograms.Scan([_dir], links, _ => true, icons: icons, windowsDir: @"C:\Windows");
        Assert.Equal([1, 2, 3], found.Single(p => p.Name == "TeamSpeak").Icon);
        Assert.Null(found.Single(p => p.Name == "Plain").Icon);
    }

    [Fact]
    public async Task The_icon_goes_to_the_server_as_base64_and_a_changed_icon_is_sent_again()
    {
        var (core, server, time) = Make.Core();
        var api = new ServerApi(new HttpClient(server) { BaseAddress = new Uri("http://club.test") }, FakeServer.PcKey);
        server.On("POST", "/api/agent/programs", 200, """{"ok":true,"accepted":1}""");
        var list = new List<ProgramEntry> { new("TeamSpeak", @"C:\Apps\TS\ts.exe", [9, 8, 7]), new("Plain", @"C:\Apps\Plain\p.exe") };
        var reporter = new ProgramsReporter(api, core, () => list.ToList(), time, NullLogger<ProgramsReporter>.Instance);

        Assert.True(await reporter.TickAsync(CancellationToken.None));
        var body = server.To("/api/agent/programs").Single().Body;
        Assert.Contains(Convert.ToBase64String([9, 8, 7]), body);
        Assert.Equal(1, body.Split("\"icon\"").Length - 1);                          // иконка только у первой программы, у второй поля нет

        time.Advance(ProgramsReporter.Every + TimeSpan.FromMinutes(1));
        Assert.False(await reporter.TickAsync(CancellationToken.None));            // всё то же самое: повторно не шлём
        list[0] = list[0] with { Icon = [1, 1, 1] };                               // программа обновилась и сменила иконку
        time.Advance(ProgramsReporter.Every + TimeSpan.FromMinutes(1));
        Assert.True(await reporter.TickAsync(CancellationToken.None));
        Assert.Equal(2, server.To("/api/agent/programs").Count());
    }

    // ---------- плитка программы в меню ----------
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

    [Fact]
    public void A_program_tile_shows_its_icon_instead_of_the_letter()
    {
        WpfHost.Run(() =>
        {
            var covers = Path.Combine(_dir, "covers");
            Directory.CreateDirectory(covers);
            var withIcon = Path.Combine(covers, "1-aaaaaaaaaaaaaaaa.png");
            File.WriteAllBytes(withIcon, PngEncoder.Encode(2, 2, [0, 128, 255, 255, 0, 128, 255, 255, 0, 128, 255, 255, 0, 128, 255, 255], false));
            var agent = new FakeAgent();
            agent.Current = new AgentSnapshot(true, PcMode.GuestSession, new AccountInfo(2, "guest", "+7 *** ***-00-01", 250),
                new SessionInfo(1, 1, 2, "3 часа", DateTime.UtcNow, null, null, 250, 9000, Kind: "package"), null, 9000, "Demo", "PC-1");
            agent.Handler = r => r.Op == Ops.Apps
                ? agent.Ok(r, apps: [new AppInfo(1, "TeamSpeak 3", "apps", "Общение", null, "exe", "C:/x.exe", "aaaaaaaaaaaaaaaa", withIcon),
                                     new AppInfo(2, "Блокнот", "apps", "Другое", null, "exe", "C:/y.exe")])
                : agent.Ok(r, tariffs: []);
            var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
            var flow = new FlowController(agent, time);
            var model = new LauncherModel(flow, agent, new NoGames(), time, coversDir: covers);
            flow.PollAsync().GetAwaiter().GetResult();
            model.PollAsync().GetAwaiter().GetResult();
            var view = new LauncherView { DataContext = model };
            var window = new Window { Content = view, Width = 1920, Height = 1080, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            window.Show();
            for (var i = 0; i < 4; i++) window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => { }));
            view.UpdateLayout();

            Assert.Equal([true, false], model.AppCards.Select(c => c.HasCover));
            var tiles = Descendants<Button>(view).Where(b => b.IsVisible && b.DataContext is AppCard c && model.AppCards.Contains(c)).ToList();
            var iconTile = tiles.Single(b => ((AppCard)b.DataContext).Title == "TeamSpeak 3");
            var plainTile = tiles.Single(b => ((AppCard)b.DataContext).Title == "Блокнот");
            var iconPicture = Descendants<Image>(iconTile).Single();
            var iconLetter = Descendants<TextBlock>(iconTile).Single(t => t.Text == "T");
            var plainLetter = Descendants<TextBlock>(plainTile).Single(t => t.Text == "Б");
            Assert.NotNull(iconPicture.Source);
            Assert.Equal(Visibility.Collapsed, iconLetter.Visibility);            // есть иконка: буква не нужна
            Assert.Equal(Visibility.Visible, plainLetter.Visibility);             // нет иконки: прежняя буква
            Assert.Equal(Stretch.Uniform, iconPicture.Stretch);                    // иконка целиком, без обрезки
            window.Close();
        });
    }
}
