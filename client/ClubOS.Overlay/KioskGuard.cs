using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClubOS.Overlay;

/// <summary>
/// Перехват клавиш, которыми гость мог бы уйти с экрана блокировки: Win, Alt+Tab, Alt+Esc, Ctrl+Esc, Ctrl+Shift+Esc, Alt+F4.
/// Два набора (KioskProfile). Lock: пока виден экран блокировки, закрыто всё перечисленное. Session: пока гость играет, закрыты только
/// Win, Ctrl+Esc и Ctrl+Shift+Esc (меню «Пуск», Win+E, Win+D, диспетчер задач): они уводят на рабочий стол Windows; а Alt+Tab и Alt+F4
/// остаются, иначе игра не закроется и из неё не вернуться в меню. На рабочем столе сотрудника перехвата нет.
///
/// Чего это НЕ делает: Ctrl+Alt+Del перехватить из программы нельзя — его закрывают политиками Windows
/// (диспетчер задач, смена пользователя и т.д., см. client/deploy). Это защита от случайного и любопытного гостя,
/// а не от человека с правами администратора или с загрузочной флешкой.
///
/// Аварийный выход (только с флагом --allow-exit, нужен при проверке на живом ПК): Ctrl+Shift+F12 три раза за 3 секунды.
/// </summary>
public enum KioskProfile
{
    Lock,       // экран блокировки: закрыты все пути с экрана
    Session,    // идёт сессия гостя: закрыты только пути на рабочий стол Windows, игре нужны остальные клавиши
}

public sealed class KioskGuard : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
    private const int VK_TAB = 0x09, VK_ESCAPE = 0x1B, VK_F4 = 0x73, VK_F12 = 0x7B, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    private readonly bool _allowExit;
    private readonly HookProc _proc; // держим ссылку, иначе сборщик мусора удалит делегат, и хук упадёт
    private IntPtr _hook = IntPtr.Zero;
    private readonly Queue<long> _exitPresses = new();
    private volatile KioskProfile _profile = KioskProfile.Lock;

    public event Action? ExitRequested;

    public KioskGuard(bool allowExit)
    {
        _allowExit = allowExit;
        _proc = Callback;
    }

    public bool Active => _hook != IntPtr.Zero;

    /// <summary>Включает перехват с нужным набором. Уже включён: только меняет набор (экран блокировки сменился меню гостя или наоборот).</summary>
    public void Enable(KioskProfile profile = KioskProfile.Lock)
    {
        _profile = profile;
        if (Active) return;
        using var module = Process.GetCurrentProcess().MainModule!;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module.ModuleName), 0);
        if (_hook == IntPtr.Zero) Log.Error("Не удалось установить перехват клавиш", new System.ComponentModel.Win32Exception());
    }

    public void Disable()
    {
        if (!Active) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    /// <summary>Нужно ли проглотить нажатие. Вынесено отдельно, чтобы правило было видно в одном месте.</summary>
    internal static bool ShouldBlock(int vk, bool alt, bool ctrl, bool shift, KioskProfile profile = KioskProfile.Lock) =>
        vk is VK_LWIN or VK_RWIN
        || (profile == KioskProfile.Lock && alt && vk is VK_TAB or VK_ESCAPE or VK_F4)
        || (ctrl && vk == VK_ESCAPE);                    // Ctrl+Esc и Ctrl+Shift+Esc (диспетчер задач)

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && ((int)wParam == WM_KEYDOWN || (int)wParam == WM_SYSKEYDOWN))
        {
            var vk = Marshal.ReadInt32(lParam);
            var flags = Marshal.ReadInt32(lParam, 8);
            var alt = (flags & 0x20) != 0 || Down(VK_MENU);
            var ctrl = Down(VK_CONTROL);
            var shift = Down(VK_SHIFT);

            if (_allowExit && vk == VK_F12 && ctrl && shift && RegisterExitPress())
                ExitRequested?.Invoke();
            if (ShouldBlock(vk, alt, ctrl, shift, _profile)) return (IntPtr)1;
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private bool RegisterExitPress()
    {
        var now = Environment.TickCount64;
        _exitPresses.Enqueue(now);
        while (_exitPresses.Count > 0 && now - _exitPresses.Peek() > 3000) _exitPresses.Dequeue();
        return _exitPresses.Count >= 3;
    }

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public void Dispose() => Disable();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
