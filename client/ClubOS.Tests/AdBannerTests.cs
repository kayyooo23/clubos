using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClubOS.Overlay;
using ClubOS.Service;
using ClubOS.Shared;
using ClubOS.Shared.Ui;
using Microsoft.Extensions.Time.Testing;

namespace ClubOS.Tests;

/// <summary>Рекламные баннеры в меню входа: смена по очереди, скачивание и проверка картинок службой, показ в окне.</summary>
public sealed class AdBannerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clubos-ads-" + Guid.NewGuid().ToString("N"));

    public AdBannerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* временная папка: не страшно */ }
    }

    // ---------- вспомогательное ----------
    private string File1(int id) => Path.Combine(_dir, $"{id}-{id:x8}.png");

    private AdInfo Ad(int id, int seconds = 10, string? advertiser = null, string? erid = null, string? path = "auto") =>
        new(id, advertiser, erid, seconds, 1200, 400, $"{id:x8}", path == "auto" ? File1(id) : path);

    private static (AdBannerModel Model, FakeAgent Agent, FakeTimeProvider Time, Func<IReadOnlyList<AdInfo>> Current, Action<IReadOnlyList<AdInfo>> Set) Create(
        string dir, IReadOnlyList<AdInfo> ads)
    {
        var agent = new FakeAgent();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        var current = ads;
        agent.Handler = r => r.Op == Ops.Ads ? new IpcResponse(r.Id, true, 200, null, AgentSnapshot.Booting, Ads: current) : agent.Ok(r);
        return (new AdBannerModel(agent, time, dir), agent, time, () => current, v => current = v);
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

    // ---------- что показывается ----------
    [Fact]
    public async Task The_first_banner_is_shown_with_the_marking_caption()
    {
        var (model, _, _, _, _) = Create(_dir, [Ad(1, advertiser: "ООО «Мама Рома»", erid: "2VtzqwP12ab"), Ad(2)]);
        Assert.False(model.HasAds);
        await model.PollAsync();
        Assert.True(model.HasAds);
        Assert.Equal(1, model.Current!.Id);
        Assert.Equal("ООО «Мама Рома»  erid: 2VtzqwP12ab", model.Current.Meta);
        Assert.True(model.Current.HasMeta);
        Assert.False(model.Slides[1].HasMeta);
        Assert.Equal(File1(1), model.Current.Path);
    }

    [Fact]
    public void Only_picture_files_from_the_ads_folder_are_accepted()
    {
        var outside = Path.Combine(Path.GetTempPath(), "evil.png");
        var sibling = _dir + "-evil" + Path.DirectorySeparatorChar + "1.png";            // начало пути совпадает с папкой баннеров, но это другая папка
        var climbing = Path.Combine(_dir, "..", "x.png");
        var slides = AdBannerModel.Build(
        [
            Ad(1), Ad(2, path: outside), Ad(3, path: sibling), Ad(4, path: climbing),
            Ad(5, path: Path.Combine(_dir, "5.gif")), Ad(6, path: Path.Combine(_dir, "6.exe")), Ad(7, path: null), Ad(8, path: ""), Ad(9, path: "\0bad"),
            Ad(10, path: Path.Combine(_dir, "10.JPG")), Ad(11, path: Path.Combine(_dir, "11.jpeg")),
        ], _dir);
        Assert.Equal([1, 10, 11], slides.Select(s => s.Id));
    }

    [Fact]
    public void Time_on_screen_is_kept_in_sane_limits_text_is_cleaned_and_the_count_is_capped()
    {
        var slides = AdBannerModel.Build(
        [
            Ad(1, seconds: 0), Ad(2, seconds: 100000), Ad(3, seconds: -5), Ad(4, advertiser: "  Много\n\tпробелов  ", erid: new string('e', 500)),
        ], _dir);
        Assert.Equal([AdBannerModel.MinSeconds, AdBannerModel.MaxSeconds, AdBannerModel.MinSeconds], slides.Take(3).Select(s => s.Seconds));
        Assert.Equal("Много пробелов  erid: " + new string('e', 80), slides[3].Meta);

        var many = Enumerable.Range(1, 40).Select(i => Ad(i)).ToList();
        Assert.Equal(AdBannerModel.MaxSlides, AdBannerModel.Build(many, _dir).Count);
    }

    // ---------- смена баннеров ----------
    [Fact]
    public async Task Several_banners_replace_each_other_each_for_its_own_number_of_seconds_and_come_round_again()
    {
        var (model, _, time, _, _) = Create(_dir, [Ad(1, 5), Ad(2, 8), Ad(3, 10)]);
        await model.PollAsync();
        Assert.Equal(1, model.Current!.Id);

        time.Advance(TimeSpan.FromSeconds(4)); await model.PollAsync();
        Assert.Equal(1, model.Current!.Id);                       // пять секунд ещё не прошло
        time.Advance(TimeSpan.FromSeconds(1)); await model.PollAsync();
        Assert.Equal(2, model.Current!.Id);
        time.Advance(TimeSpan.FromSeconds(7)); await model.PollAsync();
        Assert.Equal(2, model.Current!.Id);
        time.Advance(TimeSpan.FromSeconds(1)); await model.PollAsync();
        Assert.Equal(3, model.Current!.Id);
        time.Advance(TimeSpan.FromSeconds(10)); await model.PollAsync();
        Assert.Equal(1, model.Current!.Id);                       // по кругу
    }

    [Fact]
    public async Task A_lone_banner_stays_and_does_not_flicker()
    {
        var (model, _, time, _, _) = Create(_dir, [Ad(1, 5)]);
        await model.PollAsync();
        var changes = 0;
        model.PropertyChanged += (_, _) => changes++;
        for (var i = 0; i < 30; i++) { time.Advance(TimeSpan.FromSeconds(1)); await model.PollAsync(); }
        Assert.Equal(1, model.Current!.Id);
        Assert.Equal(0, changes);
    }

    // ---------- когда спрашиваем службу ----------
    [Fact]
    public async Task The_service_is_asked_once_a_minute_not_every_second()
    {
        var (model, agent, time, _, _) = Create(_dir, [Ad(1), Ad(2)]);
        await model.PollAsync();
        for (var i = 0; i < 30; i++) { time.Advance(TimeSpan.FromSeconds(1)); await model.PollAsync(); }
        Assert.Single(agent.Ops(Ops.Ads));
        time.Advance(TimeSpan.FromSeconds(31));
        await model.PollAsync();
        Assert.Equal(2, agent.Ops(Ops.Ads).Count());
    }

    [Fact]
    public async Task A_failed_refresh_keeps_the_old_banners_and_retries_soon()
    {
        var (model, agent, time, _, _) = Create(_dir, [Ad(1, 120), Ad(2, 120)]);
        await model.PollAsync();
        agent.Handler = r => IpcResponse.Fail(r.Id, 0, "Нет связи с сервером");
        time.Advance(TimeSpan.FromMinutes(1));
        await model.PollAsync();
        Assert.Equal(1, model.Current!.Id);                       // баннер не пропал
        var asked = agent.Ops(Ops.Ads).Count();
        time.Advance(TimeSpan.FromSeconds(5));
        await model.PollAsync();
        Assert.Equal(asked, agent.Ops(Ops.Ads).Count());          // сразу заново не долбим
        agent.Handler = r => new IpcResponse(r.Id, true, 200, null, AgentSnapshot.Booting, Ads: [Ad(7)]);
        time.Advance(TimeSpan.FromSeconds(11));
        await model.PollAsync();
        Assert.Equal(7, model.Current!.Id);
    }

    [Fact]
    public async Task When_the_club_turns_ads_off_the_banner_disappears_on_the_next_refresh()
    {
        var (model, _, time, _, set) = Create(_dir, [Ad(1), Ad(2)]);
        await model.PollAsync();
        Assert.True(model.HasAds);
        set([]);
        time.Advance(TimeSpan.FromMinutes(1));
        await model.PollAsync();
        Assert.False(model.HasAds);
        Assert.Null(model.Current);
    }

    [Fact]
    public async Task A_refresh_keeps_the_banner_on_screen_and_its_timer_when_it_is_still_in_the_list()
    {
        var (model, _, time, _, set) = Create(_dir, [Ad(1, 120), Ad(2, 120)]);
        await model.PollAsync();
        var shown = model.Current;
        var currentChanges = 0;
        model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AdBannerModel.Current)) currentChanges++; };
        time.Advance(TimeSpan.FromSeconds(61));
        set([Ad(1, 120), Ad(2, 120), Ad(3, 120)]);                 // добавили третий
        await model.PollAsync();
        Assert.Equal(3, model.Slides.Count);
        Assert.Same(shown, model.Current);
        Assert.Equal(0, currentChanges);
    }

    [Fact]
    public async Task When_the_shown_banner_leaves_the_list_the_first_of_the_new_list_is_shown()
    {
        var (model, _, time, _, set) = Create(_dir, [Ad(1, 120), Ad(2, 120)]);
        await model.PollAsync();
        time.Advance(TimeSpan.FromMinutes(1));
        set([Ad(5, 120), Ad(6, 120)]);
        await model.PollAsync();
        Assert.Equal(5, model.Current!.Id);
    }

    [Fact]
    public async Task While_the_service_is_still_downloading_pictures_it_is_asked_again_in_a_few_seconds()
    {
        var (model, agent, time, _, set) = Create(_dir, [Ad(1, path: null)]);
        await model.PollAsync();
        Assert.False(model.HasAds);                                // картинки ещё нет: показывать нечего
        set([Ad(1)]);
        time.Advance(TimeSpan.FromSeconds(3)); await model.PollAsync();
        Assert.Single(agent.Ops(Ops.Ads));                         // рано
        time.Advance(TimeSpan.FromSeconds(3)); await model.PollAsync();
        Assert.Equal(2, agent.Ops(Ops.Ads).Count());
        Assert.True(model.HasAds);
    }

    [Fact]
    public async Task A_picture_that_cannot_be_shown_leaves_the_rotation_until_the_server_sends_a_new_version()
    {
        var (model, _, time, _, set) = Create(_dir, [Ad(1, 120), Ad(2, 120)]);
        await model.PollAsync();
        model.MarkBroken(model.Current!);
        Assert.Equal(2, model.Current!.Id);
        Assert.Equal([2], model.Slides.Select(s => s.Id));

        time.Advance(TimeSpan.FromMinutes(1));
        await model.PollAsync();                                   // тот же испорченный баннер снова пришёл в списке
        Assert.Equal([2], model.Slides.Select(s => s.Id));

        set([Ad(1, 120) with { Version = "0000beef" }, Ad(2, 120)]);   // а это уже другая картинка
        time.Advance(TimeSpan.FromMinutes(1));
        await model.PollAsync();
        Assert.Equal([1, 2], model.Slides.Select(s => s.Id).OrderBy(i => i));
    }

    // ---------- меню: когда баннеры вообще нужны ----------
    [Fact]
    public async Task Banners_are_requested_only_on_the_login_screen_not_in_the_cabinet_or_during_a_game()
    {
        var agent = new FakeAgent();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        agent.Handler = r => r.Op == Ops.Ads ? new IpcResponse(r.Id, true, 200, null, AgentSnapshot.Booting, Ads: [Ad(1)]) : agent.Ok(r, tariffs: [], apps: []);
        agent.Current = new AgentSnapshot(true, PcMode.Locked, null, null, null, null);
        var flow = new FlowController(agent, time);
        var launcher = new LauncherModel(flow, agent, new NoGames(), time, _dir);
        await flow.PollAsync();
        await launcher.PollAsync();
        Assert.Single(agent.Ops(Ops.Ads));

        agent.Current = new AgentSnapshot(true, PcMode.Locked, new AccountInfo(2, "guest", "+7 *** ***-00-01", 500), null, null, null);
        await flow.PollAsync();
        time.Advance(TimeSpan.FromMinutes(5));
        await launcher.PollAsync();
        Assert.Single(agent.Ops(Ops.Ads));                         // гость в кабинете: баннер не нужен
    }

    // ---------- служба: скачивание и проверка ----------
    private (AgentCore Core, FakeServer Server) Service() { var (core, server, _) = Make.Core(adsDir: _dir); return (core, server); }

    private static string AdsJson(params (int Id, string Version, int Seconds)[] ads) =>
        "[" + string.Join(",", ads.Select(a => $$"""{"id":{{a.Id}},"advertiser":"ООО Ромашка","erid":"abc","seconds":{{a.Seconds}},"width":1200,"height":400,"version":"{{a.Version}}"}""")) + "]";

    [Fact]
    public async Task The_service_downloads_the_picture_once_checks_it_and_hands_the_overlay_a_local_file()
    {
        var (core, server) = Service();
        var png = PngBytes(1);
        server.On("GET", "/api/agent/state", 200, Samples.Cabinet).On("POST", "/api/auth/login", 200, Samples.LoginOk)
              .On("GET", "/api/agent/ads", 200, AdsJson((1, VersionOf(png), 8)))
              .OnBytes("GET", "/api/agent/ads/1/image", 200, png);
        await core.HandleAsync(new IpcRequest(1, Ops.Login, Phone: "+79000000001", Password: "p"));

        var resp = await core.HandleAsync(new IpcRequest(2, Ops.Ads));
        Assert.True(resp.Ok);
        var ad = Assert.Single(resp.Ads!);
        Assert.Equal(("ООО Ромашка", "abc", 8), (ad.Advertiser, ad.Erid, ad.Seconds));
        Assert.Equal(Path.Combine(_dir, $"1-{VersionOf(png)}.png"), ad.Path);
        Assert.Equal(png, File.ReadAllBytes(ad.Path!));

        var seen = server.To("/api/agent/ads/1/image").Single();
        Assert.Equal(FakeServer.PcKey, seen.PcKey);
        Assert.Null(seen.Auth);                                    // картинки отдаются по ключу ПК, токен гостя им не нужен

        await core.HandleAsync(new IpcRequest(3, Ops.Ads));
        Assert.Single(server.To("/api/agent/ads/1/image"));        // файл уже лежит: второй раз не качаем
    }

    [Fact]
    public async Task A_replaced_picture_is_downloaded_again_and_the_old_file_goes_away_but_foreign_files_stay()
    {
        var (core, server) = Service();
        var first = PngBytes(1);
        var second = JpegBytes(2);
        File.WriteAllText(Path.Combine(_dir, "readme.txt"), "не наше");
        server.On("GET", "/api/agent/ads", 200, AdsJson((1, VersionOf(first), 10))).OnBytes("GET", "/api/agent/ads/1/image", 200, first);
        var one = await core.HandleAsync(new IpcRequest(1, Ops.Ads));
        Assert.True(File.Exists(one.Ads![0].Path));

        server.On("GET", "/api/agent/ads", 200, AdsJson((1, VersionOf(second), 10))).OnBytes("GET", "/api/agent/ads/1/image", 200, second);
        var two = await core.HandleAsync(new IpcRequest(2, Ops.Ads));
        Assert.EndsWith(".jpg", two.Ads![0].Path);                 // тип определяется по самим байтам
        Assert.True(File.Exists(two.Ads[0].Path));
        Assert.False(File.Exists(one.Ads[0].Path));
        Assert.True(File.Exists(Path.Combine(_dir, "readme.txt")));
    }

    [Fact]
    public async Task A_picture_with_a_wrong_fingerprint_a_wrong_type_or_a_huge_size_is_not_used()
    {
        var (core, server) = Service();
        var good = PngBytes(1);
        var gif = "GIF89a"u8.ToArray().Concat(new byte[500]).ToArray();
        var huge = PngBytes(3).Concat(new byte[AdImageCache.MaxBytes]).ToArray();
        server.On("GET", "/api/agent/ads", 200, AdsJson(
                (1, VersionOf(good), 10),                          // отпечаток не тот: сервер прислал не то, что обещал
                (2, VersionOf(gif), 10),                           // не PNG и не JPEG
                (3, VersionOf(huge), 10)))                         // больше двух мегабайт
              .OnBytes("GET", "/api/agent/ads/1/image", 200, PngBytes(99))
              .OnBytes("GET", "/api/agent/ads/2/image", 200, gif)
              .OnBytes("GET", "/api/agent/ads/3/image", 200, huge);
        var resp = await core.HandleAsync(new IpcRequest(1, Ops.Ads));
        Assert.True(resp.Ok);
        Assert.Equal(3, resp.Ads!.Count);
        Assert.All(resp.Ads, a => Assert.Null(a.Path));
        Assert.Empty(Directory.EnumerateFiles(_dir));
    }

    [Fact]
    public async Task Unsafe_ids_and_versions_from_the_list_are_ignored_so_a_file_name_cannot_climb_out_of_the_folder()
    {
        var (core, server) = Service();
        var png = PngBytes(1);
        server.On("GET", "/api/agent/ads", 200, AdsJson((1, @"..\\..\\evil", 10), (2, "ABCDEF12", 10), (3, "zz", 10), (0, VersionOf(png), 10), (-4, VersionOf(png), 10)))
              .OnBytes("GET", "/api/agent/ads/1/image", 200, png);
        var resp = await core.HandleAsync(new IpcRequest(1, Ops.Ads));
        Assert.Empty(resp.Ads!);
        Assert.DoesNotContain(server.Requests, r => r.Path.Contains("/image"));
    }

    [Fact]
    public async Task One_picture_that_fails_to_download_does_not_hide_the_others()
    {
        var (core, server) = Service();
        var a = PngBytes(1);
        var b = PngBytes(2);
        server.On("GET", "/api/agent/ads", 200, AdsJson((1, VersionOf(a), 10), (2, VersionOf(b), 10)))
              .OnBytes("GET", "/api/agent/ads/1/image", 200, a)
              .OnBytes("GET", "/api/agent/ads/2/image", 500, "{}"u8.ToArray());
        var resp = await core.HandleAsync(new IpcRequest(1, Ops.Ads));
        Assert.NotNull(resp.Ads![0].Path);
        Assert.Null(resp.Ads[1].Path);                             // оверлей спросит ещё раз через несколько секунд
    }

    [Fact]
    public async Task Turning_ads_off_removes_the_pictures_from_the_pc()
    {
        var (core, server) = Service();
        var png = PngBytes(1);
        server.On("GET", "/api/agent/ads", 200, AdsJson((1, VersionOf(png), 10))).OnBytes("GET", "/api/agent/ads/1/image", 200, png);
        await core.HandleAsync(new IpcRequest(1, Ops.Ads));
        Assert.Single(Directory.EnumerateFiles(_dir));
        server.On("GET", "/api/agent/ads", 200, "[]");
        var off = await core.HandleAsync(new IpcRequest(2, Ops.Ads));
        Assert.Empty(off.Ads!);
        Assert.Empty(Directory.EnumerateFiles(_dir));
    }

    [Fact]
    public async Task A_failure_of_the_ad_list_does_not_log_the_guest_out_and_keeps_the_files()
    {
        var (core, server) = Service();
        var png = PngBytes(1);
        server.On("GET", "/api/agent/state", 200, Samples.Cabinet).On("POST", "/api/auth/login", 200, Samples.LoginOk)
              .On("GET", "/api/agent/ads", 200, AdsJson((1, VersionOf(png), 10))).OnBytes("GET", "/api/agent/ads/1/image", 200, png);
        await core.HandleAsync(new IpcRequest(1, Ops.Login, Phone: "+79000000001", Password: "p"));
        await core.HandleAsync(new IpcRequest(2, Ops.Ads));

        server.On("GET", "/api/agent/ads", 401, Samples.Error("Неверный ключ ПК"));        // даже 401 по рекламе не должен сбрасывать вход гостя
        var failed = await core.HandleAsync(new IpcRequest(3, Ops.Ads));
        Assert.False(failed.Ok);
        Assert.Equal("guest", failed.Snapshot!.Account?.Role);
        Assert.Single(Directory.EnumerateFiles(_dir));             // сбой связи не стирает картинки: после восстановления не качать заново
    }

    [Fact]
    public async Task Downloading_pictures_does_not_hold_up_the_guest_login()
    {
        var (core, server) = Service();
        var png = PngBytes(1);
        var release = new TaskCompletionSource();
        server.On("GET", "/api/agent/state", 200, Samples.Cabinet).On("POST", "/api/auth/login", 200, Samples.LoginOk)
              .On("GET", "/api/agent/ads", 200, AdsJson((1, VersionOf(png), 10)))
              .OnBytes("GET", "/api/agent/ads/1/image", _ => { release.Task.GetAwaiter().GetResult(); return (200, png); });
        var ads = Task.Run(() => core.HandleAsync(new IpcRequest(1, Ops.Ads)));
        await Task.Delay(100);                                     // картинка «качается»
        var login = await core.HandleAsync(new IpcRequest(2, Ops.Login, Phone: "+79000000001", Password: "p")).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(login.Ok);
        release.SetResult();
        Assert.True((await ads).Ok);
    }

    // ---------- окно ----------
    private sealed class NoGames : IAppLauncher
    {
        public bool IsAvailable(AppInfo app) => true;
        public string? Launch(AppInfo app) => null;
    }

    private static void WritePng(string path, Color color)
    {
        var pixels = new byte[8 * 8 * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = color.B; pixels[i + 1] = color.G; pixels[i + 2] = color.R; pixels[i + 3] = 255; }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, pixels, 8 * 4)));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static void Pump(Window window) =>
        window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => { }));

    [Fact]
    public void The_screensaver_shows_banners_only_when_there_are_ads_and_switches_between_them()
    {
        WritePng(File1(1), Colors.Red);
        WritePng(File1(2), Colors.Blue);
        WpfHost.Run(() =>
        {
            var agent = new FakeAgent();
            var ads = new List<AdInfo>();
            agent.Handler = r => r.Op == Ops.Ads
                ? new IpcResponse(r.Id, true, 200, null, AgentSnapshot.Booting, Ads: ads)
                : agent.Ok(r, tariffs: [], apps: []);
            agent.Current = new AgentSnapshot(true, PcMode.Locked, null, null, null, null);
            var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
            var flow = new FlowController(agent, time);
            var model = new LauncherModel(flow, agent, new NoGames(), time, _dir);
            var window = new LockWindow(flow, model, kiosk: false)
            {
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
            };
            window.Show();
            Pump(window);
            var banner = (AttractView)window.FindName("Attract");

            model.PollAsync().GetAwaiter().GetResult();                      // рекламы нет
            Pump(window);
            Assert.Equal(Visibility.Collapsed, banner.Visibility);
            Assert.Null(banner.Shown);

            ads.Add(Ad(1, 5, "ООО Ромашка", "abc"));
            ads.Add(Ad(2, 5));
            time.Advance(TimeSpan.FromMinutes(2));
            model.PollAsync().GetAwaiter().GetResult();                      // реклама появилась
            Pump(window);
            Assert.Equal(Visibility.Visible, banner.Visibility);
            Assert.Equal(1, banner.Shown!.Id);
            Assert.Equal(2, banner.DotCount);
            Assert.Equal("ООО Ромашка  erid: abc", banner.Shown.Meta);

            time.Advance(TimeSpan.FromSeconds(5));
            model.PollAsync().GetAwaiter().GetResult();                      // пять секунд прошло: следующий баннер
            Pump(window);
            Assert.Equal(2, banner.Shown!.Id);

            ads.Clear();
            time.Advance(TimeSpan.FromMinutes(2));
            model.PollAsync().GetAwaiter().GetResult();                      // клуб выключил рекламу
            Pump(window);
            Assert.Equal(Visibility.Collapsed, banner.Visibility);
            Assert.Null(banner.Shown);
            window.AllowClose = true;
            window.Close();
        });
    }

    [Fact]
    public void A_corrupt_picture_is_skipped_by_the_window_and_the_next_banner_is_shown()
    {
        File.WriteAllBytes(File1(1), PngBytes(5));                           // по виду PNG, а внутри мусор
        WritePng(File1(2), Colors.Blue);
        WpfHost.Run(() =>
        {
            var agent = new FakeAgent();
            agent.Handler = r => r.Op == Ops.Ads
                ? new IpcResponse(r.Id, true, 200, null, AgentSnapshot.Booting, Ads: [Ad(1, 120), Ad(2, 120)])
                : agent.Ok(r, tariffs: [], apps: []);
            agent.Current = new AgentSnapshot(true, PcMode.Locked, null, null, null, null);
            var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
            var flow = new FlowController(agent, time);
            var model = new LauncherModel(flow, agent, new NoGames(), time, _dir);
            var window = new LockWindow(flow, model, kiosk: false)
            {
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
            };
            window.Show();
            Pump(window);
            var banner = (AttractView)window.FindName("Attract");
            model.PollAsync().GetAwaiter().GetResult();
            Pump(window);
            Assert.Equal(2, banner.Shown!.Id);
            Assert.Equal([2], model.Ads.Slides.Select(s => s.Id));
            window.AllowClose = true;
            window.Close();
        });
    }
}
