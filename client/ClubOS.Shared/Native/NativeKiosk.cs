namespace ClubOS.Shared.Native;

/// <summary>
/// Перехват клавиш киоска в нативной библиотеке (C++): низкоуровневый хук клавиатуры живёт в ней, правило «что глотать» тоже (kiosk_rules.hpp).
/// Вызывать из потока с циклом сообщений (у WPF это главный поток). Если библиотеки нет, перехват делает KioskGuard на C#.
/// </summary>
public static class NativeKiosk
{
    public const int Lock = Imports.KioskLock, Session = Imports.KioskSession;

    // Делегат хранится в поле: пока он нужен нативной части, сборщик мусора не должен его удалить.
    private static Imports.ExitCallback? _callback;
    private static Action? _onExit;

    public static bool IsAvailable => ClubosNative.IsAvailable;

    /// <summary>Включает перехват (или меняет набор клавиш, если уже включён). false: хук не встал, osError = код Windows.</summary>
    public static bool Enable(int profile, bool allowExit, Action? onExit, out int osError)
    {
        _onExit = onExit;
        _callback ??= () => _onExit?.Invoke();
        var code = Imports.clubos_kiosk_enable(profile, allowExit ? 1 : 0, _callback, out var error);
        osError = (int)error;
        return code == 0;
    }

    public static void SetProfile(int profile) => Imports.clubos_kiosk_set_profile(profile);

    public static void Disable() => Imports.clubos_kiosk_disable();

    public static bool Active => Imports.clubos_kiosk_active() != 0;

    /// <summary>Правило перехвата: нужно ли проглотить нажатие (так же решает хук внутри библиотеки).</summary>
    public static bool ShouldBlock(int vk, bool alt, bool ctrl, bool shift, int profile) =>
        Imports.clubos_kiosk_should_block(vk, alt ? 1 : 0, ctrl ? 1 : 0, shift ? 1 : 0, profile) != 0;
}
