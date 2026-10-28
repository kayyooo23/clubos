using System.Runtime.InteropServices;

namespace ClubOS.Shared.Native;

/// <summary>Все вызовы clubos_native.dll в одном месте: сигнатуры и структуры должны совпадать с include/clubos_native.h.</summary>
internal static class Imports
{
    private const string Library = "clubos_native";

    public const int KioskLock = 0, KioskSession = 1;
    public const int MaxDisks = 16;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void ExitCallback();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WindowInfo
    {
        public long Handle;
        public int Pid, Width, Height, Minimized;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ProcessName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string ClassName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct Disk
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)] public string Name;
        public int TotalGb, FreeGb;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct Hardware
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Cpu;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Gpu;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 48)] public string Ip;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 24)] public string Mac;
        public int RamGb, DiskCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxDisks)] public Disk[] Disks;
    }

    [DllImport(Library, ExactSpelling = true)]
    public static extern int clubos_abi_version();

    // хеши блоков
    [DllImport(Library, ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int clubos_hash_blocks(string path, long expectedLength, int blockSize, byte[]? digests, long capacity, IntPtr cancel, out uint osError);

    // перехват клавиш
    [DllImport(Library, ExactSpelling = true)]
    public static extern int clubos_kiosk_enable(int profile, int allowExit, ExitCallback? onExit, out uint osError);

    [DllImport(Library, ExactSpelling = true)] public static extern void clubos_kiosk_set_profile(int profile);
    [DllImport(Library, ExactSpelling = true)] public static extern void clubos_kiosk_disable();
    [DllImport(Library, ExactSpelling = true)] public static extern int clubos_kiosk_active();
    [DllImport(Library, ExactSpelling = true)] public static extern int clubos_kiosk_should_block(int vk, int alt, int ctrl, int shift, int profile);

    // окна и процессы
    [DllImport(Library, ExactSpelling = true)]
    public static extern int clubos_list_windows([Out] WindowInfo[]? windows, int capacity, out int count);

    [DllImport(Library, ExactSpelling = true)]
    public static extern int clubos_foreground_window(out WindowInfo window);

    [DllImport(Library, ExactSpelling = true)] public static extern int clubos_bring_to_front(long handle);
    [DllImport(Library, ExactSpelling = true)] public static extern int clubos_close_window(long handle);
    [DllImport(Library, ExactSpelling = true)] public static extern int clubos_notification_state();

    // характеристики ПК
    [DllImport(Library, ExactSpelling = true)]
    public static extern int clubos_collect_hardware(out Hardware hardware);

    [DllImport(Library, ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int clubos_network([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder ip, [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder mac);
}
