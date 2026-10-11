using ClubOS.Overlay;

namespace ClubOS.Tests;

public class KioskGuardTests
{
    private const int Tab = 0x09, Esc = 0x1B, F4 = 0x73, LWin = 0x5B, RWin = 0x5C, C = 0x43, A = 0x41, Enter = 0x0D, Space = 0x20, Menu = 0x12;

    [Theory]
    [InlineData(LWin, false, false, false)]
    [InlineData(RWin, false, false, false)]
    [InlineData(Tab, true, false, false)]      // Alt+Tab
    [InlineData(Esc, true, false, false)]      // Alt+Esc
    [InlineData(F4, true, false, false)]       // Alt+F4
    [InlineData(Esc, false, true, false)]      // Ctrl+Esc (меню Пуск)
    [InlineData(Esc, false, true, true)]       // Ctrl+Shift+Esc (диспетчер задач)
    [InlineData(Tab, true, false, true)]       // Alt+Shift+Tab
    public void Escape_routes_are_blocked(int vk, bool alt, bool ctrl, bool shift) =>
        Assert.True(KioskGuard.ShouldBlock(vk, alt, ctrl, shift));

    [Theory]
    [InlineData(C, false, true, false)]        // Ctrl+C
    [InlineData(A, false, true, false)]        // Ctrl+A в поле ввода
    [InlineData(Tab, false, false, false)]     // Tab между полями формы
    [InlineData(Tab, false, false, true)]      // Shift+Tab
    [InlineData(Esc, false, false, false)]     // просто Esc
    [InlineData(F4, false, false, false)]      // просто F4
    [InlineData(Enter, false, false, false)]
    [InlineData(Space, false, false, false)]
    [InlineData(Menu, false, false, false)]    // сам по себе Alt
    [InlineData(0x70, false, false, false)]    // F1
    public void Normal_typing_is_never_blocked(int vk, bool alt, bool ctrl, bool shift) =>
        Assert.False(KioskGuard.ShouldBlock(vk, alt, ctrl, shift));
}
