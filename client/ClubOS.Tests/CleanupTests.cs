using System.Diagnostics;
using ClubOS.Service.Cleanup;
using ClubOS.Shared;

namespace ClubOS.Tests;

public sealed class FakeOps : ISystemOps
{
    public ConsoleUser? User { get; set; }
    public List<ProcessEntry> Procs { get; } = [];
    public List<int> KillLog { get; } = [];
    public HashSet<int> Unkillable { get; } = [];
    public Dictionary<int, ProcessEntry> RespawnOnKill { get; } = [];
    public int SteamResets { get; private set; }
    private int _next = 1000;

    public ConsoleUser? GetConsoleUser() => User;
    public IReadOnlyList<ProcessEntry> ListProcesses() => Procs.ToList();
    public bool KillTree(int pid)
    {
        KillLog.Add(pid);
        if (Unkillable.Contains(pid)) return false;
        Procs.RemoveAll(p => p.Id == pid);
        if (RespawnOnKill.Remove(pid, out var again)) Procs.Add(again);
        return true;
    }
    public void ResetSteamLogin(ConsoleUser user, List<string> report) => SteamResets++;
    public ProcessEntry Add(string name, int session = 1, int? id = null, DateTime? started = null)
    {
        var p = new ProcessEntry(id ?? _next++, name, session, started ?? new DateTime(2026, 10, 7, 9, 0, 0).AddSeconds(_next));
        Procs.Add(p);
        return p;
    }
}

public sealed class CleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "clubos-cleanup-" + Guid.NewGuid().ToString("N"));
    private readonly string _profile;
    private readonly FakeOps _ops = new();

    public CleanupTests()
    {
        _profile = Path.Combine(_root, "guest");
        Directory.CreateDirectory(_profile);
        _ops.User = new ConsoleUser("guest", "S-1-5-21-1", _profile, 1);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private SessionCleaner Cleaner(CleanupMode mode = CleanupMode.On, string? guest = "guest", string[]? extra = null, string[]? keep = null) =>
        new(_ops, new CleanupOptions(mode, guest, _root, extra ?? [], keep ?? []), null, TimeSpan.Zero);

    private string Touch(string relative, string text = "x")
    {
        var full = Path.Combine(_profile, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return full;
    }

    // ---------- кого вообще можно чистить ----------
    [Fact]
    public void Nothing_happens_when_cleanup_is_off_or_the_guest_user_is_not_set()
    {
        var chrome = Touch(@"AppData\Local\Google\Chrome\User Data\Default\Cookies");
        _ops.Add("steam");
        foreach (var cleaner in new[] { Cleaner(CleanupMode.Off), Cleaner(guest: null), Cleaner(guest: "  ") })
            Assert.False(cleaner.Clean("тест").Ran);
        Assert.Empty(_ops.KillLog);
        Assert.True(File.Exists(chrome));
    }

    [Fact]
    public void Another_windows_user_in_the_console_is_never_touched()
    {
        // вошёл администратор, а гостевой пользователь другой: ни процессы, ни файлы трогать нельзя
        _ops.User = new ConsoleUser("admin", "S-1-5-21-2", _profile, 1);
        var cookies = Touch(@"AppData\Local\Google\Chrome\User Data\Default\Cookies");
        _ops.Add("chrome");
        var r = Cleaner().Clean("тест");
        Assert.False(r.Ran);
        Assert.Contains("admin", r.Reason);
        Assert.Empty(_ops.KillLog);
        Assert.True(File.Exists(cookies));
        Assert.False(Cleaner().TakeBaseline());
    }

    [Fact]
    public void Nobody_logged_in_means_nothing_to_do()
    {
        _ops.User = null;
        Assert.False(Cleaner().Clean("тест").Ran);
    }

    [Fact]
    public void User_name_comparison_ignores_case()
    {
        _ops.User = new ConsoleUser("GUEST", "S-1", _profile, 1);
        Assert.True(Cleaner(guest: "guest").Clean("тест").Ran);
    }

    // ---------- процессы ----------
    [Fact]
    public void Programs_started_after_the_clean_snapshot_are_closed_and_the_old_ones_stay()
    {
        var explorer = _ops.Add("explorer");
        var antivirus = _ops.Add("MyAntivirusTray");        // была при загрузке ПК: админ так настроил
        var cleaner = Cleaner();
        Assert.True(cleaner.TakeBaseline());

        var steam = _ops.Add("steam");
        var game = _ops.Add("dota2");
        var odd = _ops.Add("SomeGuestTool");
        var r = cleaner.Clean("сессия закончилась");

        Assert.True(r.Ran && !r.DryRun);
        Assert.Equal([steam.Id, game.Id, odd.Id], _ops.KillLog);
        Assert.Contains(_ops.Procs, p => p.Id == explorer.Id);
        Assert.Contains(_ops.Procs, p => p.Id == antivirus.Id);
        Assert.Equal(3, r.Killed.Count);
    }

    [Fact]
    public void System_processes_and_clubos_itself_are_never_closed_even_when_new()
    {
        var cleaner = Cleaner();
        cleaner.TakeBaseline();
        var keep = new[] { "explorer", "dwm", "csrss", "svchost", "ClubOS.Overlay", "ClubOS.Service", "SearchHost", "RuntimeBroker" }
            .Select(n => _ops.Add(n)).ToList();
        var mine = _ops.Add("my-launcher");
        cleaner.Clean("тест");
        Assert.Equal([mine.Id], _ops.KillLog);
        Assert.All(keep, k => Assert.Contains(_ops.Procs, p => p.Id == k.Id));
    }

    [Fact]
    public void Processes_from_other_sessions_and_the_cleaner_own_process_are_ignored()
    {
        var cleaner = Cleaner();
        cleaner.TakeBaseline();
        _ops.Add("chrome", session: 0);                     // служебная сессия
        _ops.Add("chrome", session: 2);                     // другой пользователь
        _ops.Add("self", id: Environment.ProcessId);
        var guestChrome = _ops.Add("chrome", session: 1);
        cleaner.Clean("тест");
        Assert.Equal([guestChrome.Id], _ops.KillLog);
    }

    [Fact]
    public void Names_from_the_keep_list_are_protected_case_insensitively()
    {
        var cleaner = Cleaner(keep: ["RGBSoftware"]);
        cleaner.TakeBaseline();
        var rgb = _ops.Add("rgbsoftware");
        var steam = _ops.Add("steam");
        cleaner.Clean("тест");
        Assert.Equal([steam.Id], _ops.KillLog);
        Assert.Contains(_ops.Procs, p => p.Id == rgb.Id);
    }

    [Fact]
    public void A_reused_process_id_with_a_different_start_time_is_not_mistaken_for_an_old_process()
    {
        var old = _ops.Add("helper", id: 500, started: new DateTime(2026, 10, 7, 8, 0, 0));
        var cleaner = Cleaner();
        cleaner.TakeBaseline();
        _ops.Procs.Remove(old);
        _ops.Add("game", id: 500, started: new DateTime(2026, 10, 7, 12, 0, 0));    // тот же номер, другой процесс
        cleaner.Clean("тест");
        Assert.Equal([500], _ops.KillLog);
    }

    [Fact]
    public void Without_a_snapshot_only_well_known_guest_programs_are_closed()
    {
        _ops.Add("steam");
        _ops.Add("Chrome");
        var unknown = _ops.Add("AdminMonitoringTool");
        var cleaner = Cleaner();
        Assert.False(cleaner.HasBaseline);
        cleaner.Clean("тест");
        Assert.Equal(2, _ops.KillLog.Count);
        Assert.Contains(_ops.Procs, p => p.Id == unknown.Id);
    }

    [Fact]
    public void Launchers_that_restart_each_other_are_closed_on_the_next_pass()
    {
        var cleaner = Cleaner();
        cleaner.TakeBaseline();
        var steam = _ops.Add("steam");
        _ops.RespawnOnKill[steam.Id] = new ProcessEntry(777, "steamwebhelper", 1, DateTime.Now);
        cleaner.Clean("тест");
        Assert.Equal([steam.Id, 777], _ops.KillLog);
        Assert.Empty(_ops.Procs);
    }

    [Fact]
    public void A_process_that_will_not_close_is_reported_and_the_rest_still_goes()
    {
        var cleaner = Cleaner();
        cleaner.TakeBaseline();
        var stubborn = _ops.Add("stubborn-guest-helper");
        _ops.Unkillable.Add(stubborn.Id);
        var chrome = _ops.Add("chrome");
        var r = cleaner.Clean("тест");
        Assert.Contains(r.Errors, e => e.Contains("stubborn-guest-helper"));
        Assert.DoesNotContain(_ops.Procs, p => p.Id == chrome.Id);
    }

    [Fact]
    public void After_a_clean_the_pc_is_the_new_baseline()
    {
        var cleaner = Cleaner();
        cleaner.TakeBaseline();
        _ops.Add("steam");
        cleaner.Clean("тест");
        _ops.KillLog.Clear();
        var stayer = _ops.Add("StartedAfterCleanup");
        cleaner.TakeBaseline();                              // сотрудник ушёл с рабочего стола: новый чистый снимок
        cleaner.Clean("тест");
        Assert.Empty(_ops.KillLog);
        Assert.Contains(_ops.Procs, p => p.Id == stayer.Id);
    }

    [Fact]
    public void Dry_run_only_reports()
    {
        var cleaner = Cleaner(CleanupMode.DryRun);
        cleaner.TakeBaseline();
        var steam = _ops.Add("steam");
        var cookies = Touch(@"AppData\Local\Google\Chrome\User Data\Default\Cookies");
        var download = Touch(@"Downloads\a.zip");
        var r = cleaner.Clean("тест");
        Assert.True(r.Ran && r.DryRun);
        Assert.Equal([$"steam ({steam.Id})"], r.Killed);
        Assert.Contains(@"AppData\Local\Google\Chrome\User Data", r.Deleted);
        Assert.Empty(_ops.KillLog);
        Assert.True(File.Exists(cookies) && File.Exists(download));
        Assert.Equal(0, _ops.SteamResets);
        Assert.Contains("ничего не удалено", r.Summary);
    }

    // ---------- файлы ----------
    [Fact]
    public void Guest_data_is_removed_and_everything_else_is_left()
    {
        var cookies = Touch(@"AppData\Local\Google\Chrome\User Data\Default\Cookies");
        var edge = Touch(@"AppData\Local\Microsoft\Edge\User Data\Default\Login Data");
        var discord = Touch(@"AppData\Roaming\discord\Local Storage\token");
        var zip = Touch(@"Downloads\a.zip");
        var doc = Touch(@"Documents\secret.docx");
        var temp = Touch(@"AppData\Local\Temp\x.tmp");
        var shortcut = Touch(@"Desktop\Game.lnk");
        var note = Touch(@"Desktop\my notes.txt");
        var other = Touch(@"AppData\Roaming\SomeOtherApp\settings.ini");     // не в списке: остаётся
        var r = Cleaner().Clean("тест");

        Assert.False(File.Exists(cookies) || File.Exists(edge) || File.Exists(discord) || File.Exists(zip) || File.Exists(doc) || File.Exists(temp) || File.Exists(note));
        Assert.False(Directory.Exists(Path.Combine(_profile, @"AppData\Local\Google\Chrome\User Data")));
        Assert.True(Directory.Exists(Path.Combine(_profile, "Downloads")), "сама папка остаётся");
        Assert.True(File.Exists(shortcut), "ярлыки на рабочем столе остаются");
        Assert.True(File.Exists(other));
        Assert.Equal(1, _ops.SteamResets);
        Assert.Empty(r.Errors);
    }

    [Fact]
    public void Extra_paths_from_the_config_are_deleted_too()
    {
        var launcher = Touch(@"AppData\Roaming\MyLauncher\session.json");
        Cleaner(extra: [@"AppData\Roaming\MyLauncher"]).Clean("тест");
        Assert.False(File.Exists(launcher));
    }

    [Theory]
    [InlineData(@"..\other")]
    [InlineData(@"..\..\windows")]
    [InlineData(@"Downloads\..\..\other")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"\\server\share")]
    [InlineData("")]
    [InlineData(".")]
    [InlineData(@"AppData\..")]
    public void Paths_that_leave_the_profile_are_refused(string bad)
    {
        var outside = Path.Combine(_root, "other");
        Directory.CreateDirectory(outside);
        var important = Path.Combine(outside, "important.txt");
        File.WriteAllText(important, "x");
        var profileMarker = Touch(@"AppData\Roaming\Marker\m.txt");
        Cleaner(extra: [bad]).Clean("тест");
        Assert.True(File.Exists(important), $"«{bad}» не должен задеть чужую папку");
        Assert.True(Directory.Exists(_profile), "сам профиль не удаляется");
        Assert.True(File.Exists(profileMarker), $"«{bad}» не должен снести лишнее внутри профиля");
        Assert.Null(SessionCleaner.ResolveInside(_profile, bad));
    }

    [Fact]
    public void A_profile_outside_the_users_folder_is_left_alone()
    {
        var elsewhereRoot = Path.Combine(Path.GetTempPath(), "clubos-elsewhere-" + Guid.NewGuid().ToString("N"));
        var elsewhere = Path.Combine(elsewhereRoot, "deep", "guest");
        Directory.CreateDirectory(Path.Combine(elsewhere, "Downloads"));
        var file = Path.Combine(elsewhere, "Downloads", "a.zip");
        File.WriteAllText(file, "x");
        try
        {
            _ops.User = new ConsoleUser("guest", "S-1", elsewhere, 1);
            var r = Cleaner().Clean("тест");
            Assert.True(File.Exists(file));
            Assert.Contains(r.Errors, e => e.Contains("не трогаем"));
        }
        finally { Directory.Delete(elsewhereRoot, true); }
    }

    [Fact]
    public void A_junction_inside_the_profile_is_removed_without_touching_its_target()
    {
        var target = Path.Combine(_root, "precious");
        Directory.CreateDirectory(target);
        var precious = Path.Combine(target, "keep.txt");
        File.WriteAllText(precious, "x");
        var link = Path.Combine(_profile, "Downloads", "link");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
        using (var p = Process.Start(psi)!) { p.StandardOutput.ReadToEnd(); p.WaitForExit(); }
        if (!Directory.Exists(link)) return;                  // в этой среде связь создать нельзя: проверять нечего

        Cleaner().Clean("тест");
        Assert.True(File.Exists(precious), "содержимое, на которое указывает ссылка, должно уцелеть");
        Assert.False(Directory.Exists(link));
    }

    [Fact]
    public void Locked_files_are_reported_and_do_not_stop_the_rest()
    {
        var locked = Touch(@"Downloads\busy.bin");
        var other = Touch(@"Pictures\p.png");
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
        var r = Cleaner().Clean("тест");
        Assert.False(File.Exists(other));
        Assert.NotEmpty(r.Errors);
    }

    // ---------- когда чистить ----------
    private static AgentSnapshot Snap(PcMode mode, bool guest = false, bool connected = true) =>
        new(connected, mode, guest ? new AccountInfo(2, "guest", "+7", 100) : null, null, null, null);

    [Fact]
    public void The_worker_cleans_exactly_when_a_guest_session_ends()
    {
        Assert.Equal(CleanupAction.Clean, CleanupWorker.Decide(Snap(PcMode.GuestSession, true), Snap(PcMode.Locked, true), true));
        Assert.Equal(CleanupAction.Clean, CleanupWorker.Decide(Snap(PcMode.GuestSession, true), Snap(PcMode.Locked), true));         // остановка по времени
        Assert.Equal(CleanupAction.None, CleanupWorker.Decide(Snap(PcMode.GuestSession, true), Snap(PcMode.GuestSession, true), true));
        Assert.Equal(CleanupAction.None, CleanupWorker.Decide(Snap(PcMode.Locked, true), Snap(PcMode.GuestSession, true), true));    // сессия началась
        Assert.Equal(CleanupAction.None, CleanupWorker.Decide(Snap(PcMode.Locked, true), Snap(PcMode.Locked), true));                // вышел из кабинета, играть не начав
        Assert.Equal(CleanupAction.None, CleanupWorker.Decide(null, Snap(PcMode.Locked, true), true));
    }

    [Fact]
    public void The_worker_takes_a_snapshot_on_a_clean_start_and_after_staff_leave_but_never_cleans_staff_programs()
    {
        Assert.Equal(CleanupAction.Rebaseline, CleanupWorker.Decide(null, Snap(PcMode.Locked), false));
        Assert.Equal(CleanupAction.None, CleanupWorker.Decide(null, Snap(PcMode.Locked), true));
        Assert.Equal(CleanupAction.None, CleanupWorker.Decide(null, Snap(PcMode.Locked, connected: false), false));            // сервер ещё не ответил
        Assert.Equal(CleanupAction.None, CleanupWorker.Decide(null, Snap(PcMode.Locked, true), false));                        // в кабинете гость: не чисто
        Assert.Equal(CleanupAction.Rebaseline, CleanupWorker.Decide(Snap(PcMode.StaffDesktop), Snap(PcMode.Locked), true));
        Assert.Equal(CleanupAction.None, CleanupWorker.Decide(Snap(PcMode.StaffDesktop), Snap(PcMode.StaffDesktop), true));
    }

    [Fact]
    public void Nothing_is_ever_cleaned_while_a_guest_is_playing()
    {
        foreach (var previous in new[] { null, Snap(PcMode.Locked), Snap(PcMode.Locked, true), Snap(PcMode.GuestSession, true), Snap(PcMode.StaffDesktop) })
            Assert.Equal(CleanupAction.None, CleanupWorker.Decide(previous, Snap(PcMode.GuestSession, true), true));
    }

    // ---------- настройки ----------
    [Fact]
    public void Config_defaults_to_dry_run_and_reads_the_cleanup_fields_from_the_file()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "agent.json");
        File.WriteAllText(path, """{"server":"https://club.test","pc_key":"k"}""");
        var cfg = AgentConfig.Load(path);
        Assert.Equal("dry-run", cfg.Cleanup);
        Assert.Null(cfg.GuestUser);

        File.WriteAllText(path, """{"server":"https://club.test","pc_key":"k","cleanup":"ON","guest_user":"guest","cleanup_paths":["AppData\\Roaming\\X"],"cleanup_keep":["rgb"]}""");
        cfg = AgentConfig.Load(path);
        Assert.Equal("on", cfg.Cleanup);
        Assert.Equal("guest", cfg.GuestUser);
        Assert.Equal([@"AppData\Roaming\X"], cfg.CleanupPaths);
        Assert.Equal(["rgb"], cfg.CleanupKeep);
    }

    [Fact]
    public void Config_with_a_wrong_cleanup_mode_is_refused_loudly()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "agent.json");
        File.WriteAllText(path, """{"server":"https://club.test","pc_key":"k","cleanup":"yes"}""");
        Assert.Throws<InvalidOperationException>(() => AgentConfig.Load(path));
    }
}
