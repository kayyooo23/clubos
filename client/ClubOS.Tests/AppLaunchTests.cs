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
