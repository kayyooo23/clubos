using ClubOS.Overlay;

namespace ClubOS.Tests;

/// <summary>Обращения к Windows, на которых держится киоск: проверяем на настоящей системе, что они работают и не падают (ничего не показывают и не закрывают).</summary>
public class NativeWindowsTests
{
    [Fact]
    public void The_window_list_has_real_windows_with_a_process_and_a_size()
    {
        var windows = NativeWindows.TopLevel();
        Assert.NotEmpty(windows);
        Assert.All(windows, w => Assert.True(w.ProcessId > 0 && w.Handle != 0 && !string.IsNullOrEmpty(w.ClassName)));
        Assert.Contains(windows, w => w.Width > 0 && w.Height > 0);
    }

    [Fact]
    public void The_windows_desktop_shell_is_recognised_by_its_process()
    {
        // на любом ПК с рабочим столом Проводник владеет окнами панели задач и рабочего стола (ClubOS по ним решает, что гость вышел на рабочий стол)
        Assert.Contains(NativeWindows.TopLevel(), w => w.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_notification_state_is_a_known_value()
    {
        Assert.InRange(NativeWindows.NotificationState(), 1, 7);
    }

    [Fact]
    public void Asking_for_the_foreground_window_does_not_fail()
    {
        var foreground = NativeWindows.Foreground();
        if (foreground is not null) Assert.True(foreground.ProcessId > 0);
    }

    [Fact]
    public void Closing_or_raising_a_window_that_does_not_exist_is_harmless()
    {
        NativeWindows.Close(0x7FFFFFF0);
        NativeWindows.BringToFront(0x7FFFFFF0);
    }
}
