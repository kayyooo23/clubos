using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ClubOS.Shared.Ui;

namespace ClubOS.Overlay;

/// <summary>Win32 для оболочки сессии гостя: какие окна открыты, какое в фокусе, вывести вперёд, закрыть, идёт ли чужое полноэкранное приложение.</summary>
internal static class NativeWindows
{
    private const int GwOwner = 4, GwlExStyle = -20, WsExToolWindow = 0x80, WmClose = 0x0010, SwRestore = 9, DwmwaCloaked = 14;

    public static int OwnPid { get; } = Environment.ProcessId;

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool on);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }

    private static string ProcessName(int pid, Dictionary<int, string> cache)
    {
        if (cache.TryGetValue(pid, out var known)) return known;
        string name;
        try { name = Process.GetProcessById(pid).ProcessName; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { name = ""; }   // процесс уже закрылся или нет доступа
        return cache[pid] = name;
    }

    private static WindowInfo? Describe(IntPtr hwnd, Dictionary<int, string> names)
    {
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out var pid);
        var cls = new StringBuilder(128);
        GetClassName(hwnd, cls, cls.Capacity);
        GetWindowRect(hwnd, out var r);
        return new WindowInfo(hwnd.ToInt64(), (int)pid, ProcessName((int)pid, names), cls.ToString(), r.Right - r.Left, r.Bottom - r.Top, IsIconic(hwnd));
    }

    /// <summary>Окно в фокусе или null, если фокуса нет.</summary>
    public static WindowInfo? Foreground() => Describe(GetForegroundWindow(), []);

    /// <summary>Видимые окна верхнего уровня, самое верхнее первым: без вспомогательных (у них есть владелец), «инструментальных» и скрытых UWP-окон.</summary>
    public static IReadOnlyList<WindowInfo> TopLevel()
    {
        var names = new Dictionary<int, string>();
        var result = new List<WindowInfo>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GwOwner) != IntPtr.Zero) return true;
            if ((GetWindowLong(hwnd, GwlExStyle) & WsExToolWindow) != 0) return true;
            if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            if (Describe(hwnd, names) is { } info) result.Add(info);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>
    /// Вывести окно вперёд и в фокус (из свёрнутого тоже), даже когда в фокусе чужая игра. Windows не даёт фоновому процессу забрать фокус,
    /// поэтому на время вызова наша нить присоединяется к нити окна в фокусе: так делают все программы, которым нужно выйти поверх игры.
    /// </summary>
    public static void BringToFront(long handle)
    {
        var hwnd = new IntPtr(handle);
        if (!IsWindow(hwnd)) return;
        if (IsIconic(hwnd)) ShowWindow(hwnd, SwRestore);
        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var ours = GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != ours && AttachThreadInput(ours, foregroundThread, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(ours, foregroundThread, false);
        }
    }

    /// <summary>Просит окно закрыться (как крестик). Не убивает процесс: Проводник сам закроет окно.</summary>
    public static void Close(long handle) => PostMessage(new IntPtr(handle), WmClose, IntPtr.Zero, IntPtr.Zero);

    /// <summary>SHQueryUserNotificationState: 2 полноэкранное приложение, 3 полноэкранное приложение Direct3D, 5 можно показывать уведомления. При сбое считаем, что полноэкранного нет.</summary>
    public static int NotificationState() => SHQueryUserNotificationState(out var state) == 0 ? state : 5;
}
