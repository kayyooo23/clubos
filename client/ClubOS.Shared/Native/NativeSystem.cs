using System.Text;
using ClubOS.Shared.Ui;

namespace ClubOS.Shared.Native;

/// <summary>Железо ПК, которое определила нативная часть. Пустая строка и 0 значат «определить не вышло»: вызывающий берёт запасной путь на C#.</summary>
public sealed record NativeHardwareSnapshot(string? Cpu, string? Gpu, int RamGb, string? Ip, string? Mac, IReadOnlyList<DiskReport> Disks);

/// <summary>Сбор характеристик ПК в нативной части (C++): процессор через CPUID, память платы, видеокарта из драйверов, диски и сетевой адаптер через Win32.</summary>
public static class NativeHardware
{
    public static bool IsAvailable => ClubosNative.IsAvailable;

    public static NativeHardwareSnapshot? Collect()
    {
        if (!IsAvailable || Imports.clubos_collect_hardware(out var hw) != 0) return null;
        var disks = (hw.Disks ?? []).Take(Math.Clamp(hw.DiskCount, 0, Imports.MaxDisks)).Select(d => new DiskReport(d.Name, d.TotalGb, d.FreeGb)).ToList();
        return new NativeHardwareSnapshot(Clean(hw.Cpu), Clean(hw.Gpu), hw.RamGb, Clean(hw.Ip), Clean(hw.Mac), disks);
    }

    /// <summary>Адрес и MAC рабочего сетевого адаптера (проводной в приоритете, виртуальные не берутся) или null, если не нашёлся.</summary>
    public static (string Ip, string? Mac)? Network()
    {
        if (!IsAvailable) return null;
        var ip = new StringBuilder(48);
        var mac = new StringBuilder(24);
        return Imports.clubos_network(ip, mac) == 1 ? (ip.ToString(), mac.Length == 0 ? null : mac.ToString()) : null;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>Окна и процессы для оболочки сессии гостя в нативной части (C++): какие окна открыты, какое в фокусе, вывести вперёд, закрыть, идёт ли чужое полноэкранное приложение.</summary>
public static class NativeWindowApi
{
    private const int InitialCapacity = 256;

    public static bool IsAvailable => ClubosNative.IsAvailable;

    private static WindowInfo Convert(Imports.WindowInfo w) => new(w.Handle, w.Pid, w.ProcessName ?? "", w.ClassName ?? "", w.Width, w.Height, w.Minimized != 0);

    /// <summary>Видимые окна верхнего уровня, самое верхнее первым (без вспомогательных, «инструментальных» и скрытых UWP-окон).</summary>
    public static IReadOnlyList<WindowInfo> TopLevel()
    {
        var capacity = InitialCapacity;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = new Imports.WindowInfo[capacity];
            if (Imports.clubos_list_windows(buffer, capacity, out var count) != 0) return [];
            if (count <= capacity) return buffer.Take(count).Select(Convert).ToList();
            capacity = count + 32;                           // окон оказалось больше буфера: повторяем с запасом
        }
        return [];
    }

    public static WindowInfo? Foreground() => Imports.clubos_foreground_window(out var w) == 1 ? Convert(w) : null;

    public static void BringToFront(long handle) => Imports.clubos_bring_to_front(handle);

    /// <summary>Просит окно закрыться (как крестик). Процесс не убивает.</summary>
    public static void Close(long handle) => Imports.clubos_close_window(handle);

    /// <summary>Состояние уведомлений Windows: 2 и 3 полноэкранное приложение, 5 можно показывать уведомления; при сбое 5.</summary>
    public static int NotificationState() => Imports.clubos_notification_state();
}
