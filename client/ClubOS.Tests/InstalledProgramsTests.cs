using ClubOS.Service;

namespace ClubOS.Tests;

/// <summary>Поиск программ по ярлыкам меню «Пуск»: настоящие программы остаются, удаление, справка и системные файлы нет.</summary>
public sealed class InstalledProgramsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clubos-test-programs-" + Guid.NewGuid().ToString("N"));

    public InstalledProgramsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* временная папка */ }
    }

    /// <summary>Ярлык-пустышка и «раскрытие» в нужный путь.</summary>
    private sealed class Links : IShortcutResolver
    {
        public Dictionary<string, string> Map { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? Target(string shortcutPath) => Map.TryGetValue(shortcutPath, out var t) ? t : null;
    }

    private string Lnk(Links links, string relative, string target)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        links.Map[path] = target;
        return path;
    }

    private static bool Exists(string path) => !path.Contains("missing", StringComparison.OrdinalIgnoreCase);

    private IReadOnlyList<ProgramEntry> Scan(Links links, int limit = 600) =>
        InstalledPrograms.Scan([_dir], links, Exists, limit, windowsDir: @"C:\Windows");

    [Fact]
    public void A_real_program_shortcut_gives_its_name_and_full_path()
    {
        var links = new Links();
        Lnk(links, @"Programs\TeamSpeak 3 Client\TeamSpeak 3 Client.lnk", @"C:\Users\katya\AppData\Local\Programs\TeamSpeak 3 Client\ts3client_win64.exe");
        var found = Scan(links);
        var program = Assert.Single(found);
        Assert.Equal("TeamSpeak 3 Client", program.Name);
        Assert.Equal(@"C:\Users\katya\AppData\Local\Programs\TeamSpeak 3 Client\ts3client_win64.exe", program.Path);
    }

    [Theory]
    [InlineData("Удалить TeamSpeak.lnk", @"C:\Apps\TS\ts.exe")]                      // название ярлыка
    [InlineData("Uninstall Game.lnk", @"C:\Apps\Game\game.exe")]
    [InlineData("Game Readme.lnk", @"C:\Apps\Game\game.exe")]
    [InlineData("Game.lnk", @"C:\Apps\Game\unins000.exe")]                            // имя файла
    [InlineData("Game.lnk", @"C:\Apps\Game\Setup.exe")]
    [InlineData("Game.lnk", @"C:\Apps\Game\update.exe")]
    [InlineData("Game.lnk", @"C:\Windows\System32\notepad.exe")]                      // системные программы
    [InlineData("Game.lnk", @"C:\Apps\Game\run.bat")]                                 // не exe
    [InlineData("Game.lnk", @"\\server\share\game.exe")]                              // сетевой путь
    [InlineData("Game.lnk", @"C:\Apps\..\Windows\game.exe")]
    [InlineData("Game.lnk", @"C:\Apps\missing\game.exe")]                             // файла уже нет
    public void Service_files_and_unsuitable_targets_are_left_out(string name, string target)
    {
        var links = new Links();
        Lnk(links, @"Programs\" + name, target);
        Assert.Empty(Scan(links));
    }

    [Fact]
    public void The_same_program_from_two_shortcuts_is_listed_once_with_the_shorter_name()
    {
        var links = new Links();
        Lnk(links, @"Start\Discord Inc\Discord Application.lnk", @"C:\Apps\Discord\discord.exe");
        Lnk(links, @"Desktop\Discord.lnk", @"C:\APPS\discord\Discord.exe");
        var program = Assert.Single(Scan(links));
        Assert.Equal("Discord", program.Name);
    }

    [Fact]
    public void The_list_is_sorted_by_name_and_capped()
    {
        var links = new Links();
        foreach (var name in new[] { "Zeta", "Alpha", "Mid" }) Lnk(links, $@"Programs\{name}.lnk", $@"C:\Apps\{name}\{name}.exe");
        Assert.Equal(["Alpha", "Mid", "Zeta"], Scan(links).Select(p => p.Name));
        Assert.Equal(["Alpha", "Mid"], Scan(links, limit: 2).Select(p => p.Name));
    }

    [Fact]
    public void A_shortcut_that_cannot_be_read_is_skipped_and_the_others_still_work()
    {
        var links = new Links();
        Lnk(links, @"Programs\Broken.lnk", @"C:\Apps\Broken\b.exe");
        links.Map.Remove(Path.Combine(_dir, @"Programs\Broken.lnk"));                  // раскрыть ярлык не удалось
        Lnk(links, @"Programs\Good.lnk", @"C:\Apps\Good\g.exe");
        Assert.Equal(["Good"], Scan(links).Select(p => p.Name));
    }

    [Fact]
    public void Shortcuts_in_all_user_profiles_are_found_but_default_profiles_are_skipped()
    {
        var users = Path.Combine(_dir, "Users");
        foreach (var user in new[] { "katya", "guest", "Default", "Public" })
            Directory.CreateDirectory(Path.Combine(users, user, "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs"));
        Directory.CreateDirectory(Path.Combine(users, "Public", "Desktop"));
        var common = Path.Combine(_dir, "Common");
        Directory.CreateDirectory(Path.Combine(common, "Programs"));

        var roots = InstalledPrograms.DefaultRoots(users, common);
        Assert.Contains(Path.Combine(common, "Programs"), roots);
        Assert.Contains(Path.Combine(users, "katya", "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs"), roots);
        Assert.Contains(Path.Combine(users, "guest", "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs"), roots);
        Assert.Contains(Path.Combine(users, "Public", "Desktop"), roots);
        Assert.DoesNotContain(roots, r => r.Contains(Path.Combine(users, "Default")));
    }
}
