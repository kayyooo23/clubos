using ClubOS.Shared.Ui;

namespace ClubOS.Tests;

/// <summary>Меню на весь экран и сообщение поверх игры: что делать с окнами, пока идёт сессия гостя, и как показать сообщение администратора.</summary>
public class ShellGuardTests
{
    private const int Own = 100;
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private static WindowInfo W(long handle, int pid, string process, string cls = "Window", int width = 1920, int height = 1080, bool minimized = false) =>
        new(handle, pid, process, cls, width, height, minimized);

    private static readonly WindowInfo Menu = W(1, Own, "ClubOS.Overlay");
    private static readonly WindowInfo Desktop = W(3, 300, "explorer", "Progman");
    private static readonly WindowInfo Taskbar = W(4, 300, "explorer", "Shell_TrayWnd", 1920, 40);
    private static readonly WindowInfo Folder = W(5, 300, "explorer", "CabinetWClass", 900, 600);
    private static readonly WindowInfo StartMenu = W(6, 400, "StartMenuExperienceHost", "Windows.UI.Core.CoreWindow", 600, 700);
    private static WindowInfo Game(long handle = 2) => W(handle, 200, "dota2");

    private static GuardDecision Decide(ShellGuard guard, WindowInfo? foreground, params WindowInfo[] windows) =>
        guard.Decide(foreground, windows, Own, T0);

    // ---------- без запуска ----------
    [Fact]
    public void A_game_in_front_is_left_alone()
    {
        Assert.Equal(GuardAction.None, Decide(new ShellGuard(), Game(), Game(), Menu, Desktop).Action);
    }

    [Fact]
    public void The_menu_in_front_is_left_alone_and_a_window_behind_it_is_not_touched_without_a_launch()
    {
        Assert.Equal(GuardAction.None, Decide(new ShellGuard(), Menu, Menu, Game(), Desktop).Action);
    }

    [Theory]
    [InlineData("desktop")]
    [InlineData("taskbar")]
    [InlineData("start")]
    [InlineData("nothing")]
    public void The_desktop_taskbar_start_menu_or_nothing_in_front_brings_the_menu_back(string what)
    {
        WindowInfo? front = what switch { "desktop" => Desktop, "taskbar" => Taskbar, "start" => StartMenu, _ => null };
        Assert.Equal(GuardAction.ShowMenu, Decide(new ShellGuard(), front, Menu, Desktop).Action);
    }

    // ---------- Проводник ----------
    [Fact]
    public void A_folder_window_is_closed_whatever_is_in_front()
    {
        var d = Decide(new ShellGuard(), Game(), Game(), Folder, Menu);
        Assert.Equal(new GuardDecision(GuardAction.CloseWindow, Folder.Handle), d);
    }

    [Fact]
    public void A_minimized_folder_window_is_not_a_reason_to_close_anything()
    {
        Assert.Equal(GuardAction.None, Decide(new ShellGuard(), Game(), Game(), W(5, 300, "explorer", "CabinetWClass", 900, 600, minimized: true)).Action);
    }

    [Fact]
    public void A_dialog_of_a_game_is_not_an_explorer_window()
    {
        var openFile = W(7, 200, "dota2", "#32770", 800, 500);        // «Открыть файл» принадлежит игре: закрыть его здесь нельзя
        Assert.Equal(GuardAction.None, Decide(new ShellGuard(), openFile, openFile, Game(), Menu).Action);
    }

    // ---------- сразу после запуска игры ----------
    [Fact]
    public void A_game_that_opened_behind_the_menu_is_pulled_to_the_front()
    {
        var guard = new ShellGuard();
        guard.Launched(T0, [Menu.Handle, Desktop.Handle]);
        var d = guard.Decide(Menu, [Menu, Game(), Desktop], Own, T0.AddSeconds(3));
        Assert.Equal(new GuardDecision(GuardAction.PullToFront, 2), d);
    }

    [Fact]
    public void Windows_that_were_open_before_the_launch_are_not_pulled()
    {
        var guard = new ShellGuard();
        guard.Launched(T0, [Menu.Handle, 2, Desktop.Handle]);        // эта «игра» уже была на экране
        Assert.Equal(GuardAction.None, guard.Decide(Menu, [Menu, Game(), Desktop], Own, T0.AddSeconds(3)).Action);
    }

    [Fact]
    public void Small_windows_our_own_windows_and_shell_windows_are_not_pulled()
    {
        var guard = new ShellGuard();
        guard.Launched(T0, [Menu.Handle]);
        var tooltip = W(8, 200, "dota2", "Tooltip", 120, 30);
        var notice = W(9, Own, "ClubOS.Overlay", "NoticeWindow");
        Assert.Equal(GuardAction.None, guard.Decide(Menu, [Menu, tooltip, notice, Taskbar, Desktop], Own, T0.AddSeconds(2)).Action);
    }

    [Fact]
    public void The_waiting_ends_when_the_game_comes_to_the_front_by_itself()
    {
        var guard = new ShellGuard();
        guard.Launched(T0, [Menu.Handle]);
        Assert.Equal(GuardAction.None, guard.Decide(Game(), [Game(), Menu], Own, T0.AddSeconds(5)).Action);     // игра вышла вперёд сама
        Assert.False(guard.InGrace(T0.AddSeconds(5)));
        Assert.Equal(GuardAction.None, guard.Decide(Menu, [Menu, Game(10)], Own, T0.AddSeconds(30)).Action);   // гость вернулся в меню: другое окно больше не тянем
    }

    [Fact]
    public void The_waiting_runs_out_after_ninety_seconds()
    {
        var guard = new ShellGuard();
        guard.Launched(T0, [Menu.Handle]);
        Assert.True(guard.InGrace(T0.AddSeconds(89)));
        Assert.Equal(GuardAction.None, guard.Decide(Menu, [Menu, Game()], Own, T0.AddSeconds(91)).Action);
        Assert.False(guard.InGrace(T0.AddSeconds(91)));
    }

    [Fact]
    public void The_desktop_during_the_launch_still_brings_the_menu_back()
    {
        var guard = new ShellGuard();
        guard.Launched(T0, [Menu.Handle, Desktop.Handle]);
        Assert.Equal(GuardAction.ShowMenu, guard.Decide(Desktop, [Desktop, Menu], Own, T0.AddSeconds(1)).Action);   // рабочий стол гостю не показываем и пока игра грузится
    }

    [Fact]
    public void Reset_forgets_the_launch()
    {
        var guard = new ShellGuard();
        guard.Launched(T0, [Menu.Handle]);
        guard.Reset();
        Assert.Equal(GuardAction.None, guard.Decide(Menu, [Menu, Game()], Own, T0.AddSeconds(2)).Action);
    }

    // ---------- как показать сообщение ----------
    [Theory]
    [InlineData(false, 3, NoticeStyle.FullScreen)]    // чужая игра в «настоящем» полноэкранном режиме
    [InlineData(false, 2, NoticeStyle.FullScreen)]    // чужое полноэкранное приложение
    [InlineData(true, 3, NoticeStyle.Corner)]         // на переднем плане наше меню на весь экран: уголок виден
    [InlineData(true, 2, NoticeStyle.Corner)]
    [InlineData(false, 5, NoticeStyle.Corner)]        // обычные окна
    [InlineData(false, 1, NoticeStyle.Corner)]
    [InlineData(false, 6, NoticeStyle.Corner)]
    [InlineData(false, 7, NoticeStyle.Corner)]
    public void A_message_goes_full_screen_only_over_a_foreign_full_screen_app(bool ours, int state, NoticeStyle expected) =>
        Assert.Equal(expected, NoticePresenter.Choose(ours, state));
}
