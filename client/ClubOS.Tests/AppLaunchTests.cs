using ClubOS.Shared;
using ClubOS.Shared.Ui;

namespace ClubOS.Tests;

public class AppLaunchTests
{
    private static AppInfo Steam(string id) => new(1, "G", "games", "X", null, "steam", id);
    private static AppInfo Exe(string path) => new(1, "G", "games", "X", null, "exe", path);

    [Fact]
    public void Steam_game_starts_through_the_steam_address()
    {
        var plan = AppLaunch.Plan(Steam("570"), out var error);
        Assert.Null(error);
        Assert.Equal("steam://rungameid/570", plan!.FileName);
        Assert.True(plan.UseShellExecute);
    }

    [Fact]
    public void Exe_starts_from_its_own_folder_without_the_shell()
    {
        var plan = AppLaunch.Plan(Exe(@"D:\Games (x86)\My Game\Bin\game.EXE"), out var error);
        Assert.Null(error);
        Assert.Equal(@"D:\Games (x86)\My Game\Bin\game.EXE", plan!.FileName);
        Assert.Equal(@"D:\Games (x86)\My Game\Bin", plan.WorkingDirectory);
        Assert.False(plan.UseShellExecute);
    }

    // ---------- браузеры открываются в приватном режиме ----------
    [Theory]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe", "--incognito")]
    [InlineData(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", "--inprivate")]
    [InlineData(@"C:\Program Files\Mozilla Firefox\firefox.exe", "-private-window")]
    [InlineData(@"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe", "--incognito")]
    [InlineData(@"C:\Users\guest\AppData\Local\Vivaldi\Application\vivaldi.exe", "--incognito")]
    [InlineData(@"C:\Users\guest\AppData\Local\Programs\Opera\launcher.exe", "--private")]
    [InlineData(@"C:\Users\guest\AppData\Local\Programs\Opera GX\opera.exe", "--private")]
    [InlineData(@"C:\Users\guest\AppData\Local\Yandex\YandexBrowser\Application\browser.exe", "--incognito")]
    [InlineData(@"D:\Apps\CHROME.EXE", "--incognito")]                                  // регистр имени файла не важен
    public void A_known_browser_starts_in_private_mode(string path, string flag)
    {
        var plan = AppLaunch.Plan(Exe(path), out var error);
        Assert.Null(error);
        Assert.Equal(flag, plan!.Arguments);
        Assert.Equal(path, plan.FileName);
        Assert.False(plan.UseShellExecute);
    }

    [Theory]
    [InlineData(@"D:\Games\game.exe")]
    [InlineData(@"C:\Program Files\TeamSpeak 3 Client\ts3client_win64.exe")]
    [InlineData(@"C:\Program Files\Discord\Discord.exe")]
    [InlineData(@"C:\Games\Tools\browser.exe")]             // «browser.exe» не из папки Яндекса: это не он
    [InlineData(@"C:\Tools\launcher.exe")]                  // «launcher.exe» не из папки Opera
    [InlineData(@"C:\Chrome\notchrome.exe")]                // совпадает не имя файла, а часть пути или имени
    [InlineData(@"C:\Users\chrome.exe\game.exe")]
    public void Anything_else_starts_without_arguments(string path) =>
        Assert.Null(AppLaunch.Plan(Exe(path), out _)!.Arguments);

    [Fact]
    public void A_steam_game_has_no_arguments()
    {
        Assert.Null(AppLaunch.Plan(Steam("570"), out _)!.Arguments);
    }

    [Theory]
    [InlineData("steam", "dota")]
    [InlineData("steam", "0570")]
    [InlineData("steam", "570 && calc")]
    [InlineData("steam", "570/../x")]
    [InlineData("steam", "")]
    [InlineData("exe", "game.exe")]
    [InlineData("exe", @"\\evil\share\game.exe")]
    [InlineData("exe", @"C:\Games\..\Windows\cmd.exe")]
    [InlineData("exe", @"C:\Games\game.bat")]
    [InlineData("exe", @"C:\Games\game.exe /c calc")]
    [InlineData("exe", @"C:\Games\\game.exe")]
    [InlineData("exe", "C:/Games/game.exe")]
    [InlineData("exe", "https://host/game.exe")]
    [InlineData("shell", "calc.exe")]
    [InlineData("", "")]
    public void Anything_that_does_not_look_like_a_game_is_refused(string kind, string target)
    {
        var plan = AppLaunch.Plan(new AppInfo(1, "G", "games", "X", null, kind, target), out var error);
        Assert.Null(plan);
        Assert.Contains("Сообщите администратору", error);
    }
}
