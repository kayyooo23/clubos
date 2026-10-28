using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using ClubOS.Shared;
using ClubOS.Shared.Native;
using Microsoft.Win32;

namespace ClubOS.Service;

/// <summary>
/// Определяет железо ПК средствами самой Windows (реестр, диски, сетевые адаптеры), без лишних библиотек.
/// Каждая часть защищена: если что-то определить не вышло, это поле остаётся пустым, а остальное отправляется.
/// Процессор, память, видеокарту, диски и сеть определяет нативная часть (C++, ClubOS.Native); чего она не определила или её нет, определяется здесь на C#.
/// </summary>
public static class HardwareCollector
{
    private const string GpuClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static HardwareReport Collect()
    {
        var native = NativeHardware.Collect();
        if (native is null) return CollectManaged();
        var (managedIp, managedMac) = native.Ip is null ? NetworkManaged() : (null, null);
        return new HardwareReport(native.Cpu ?? Cpu(), native.RamGb > 0 ? native.RamGb : Ram(), native.Gpu ?? Gpu(), Os(),
            native.Ip ?? managedIp, native.Ip is null ? managedMac : native.Mac, native.Disks.Count > 0 ? native.Disks : Disks()).Normalized();
    }

    /// <summary>Тот же отчёт целиком на C#: запасной путь без нативной библиотеки и эталон для её проверки.</summary>
    internal static HardwareReport CollectManaged()
    {
        var (ip, mac) = NetworkManaged();
        return new HardwareReport(Cpu(), Ram(), Gpu(), Os(), ip, mac, Disks()).Normalized();
    }

    private static string? Cpu()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { } parts
                ? string.Join(' ', parts) : null;   // у процессоров в названии бывают двойные пробелы
        }
        catch { return null; }
    }

    private static int? Ram()
    {
        try { return HardwareReport.ToGb(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes); }
        catch { return null; }
    }

    /// <summary>Видеокарта: из всех адаптеров берём «настоящую» (NVIDIA, Radeon), а не встроенную или базовый драйвер Microsoft.</summary>
    private static string? Gpu()
    {
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(GpuClass);
            if (cls is null) return null;
            var names = cls.GetSubKeyNames().Where(n => n.All(char.IsDigit))
                .Select(n => { using var k = cls.OpenSubKey(n); return k?.GetValue("DriverDesc") as string; })
                .Where(n => !string.IsNullOrWhiteSpace(n) && !n!.Contains("Basic", StringComparison.OrdinalIgnoreCase)).ToList();
            return names.FirstOrDefault(n => n!.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                                             || n.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
                                             || n.Contains("GeForce", StringComparison.OrdinalIgnoreCase))
                   ?? names.FirstOrDefault();
        }
        catch { return null; }
    }

    private static string? Os()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var name = key?.GetValue("ProductName") as string;
            var build = int.TryParse(key?.GetValue("CurrentBuildNumber") as string, out var b) ? b : 0;
            if (name is not null && build >= 22000) name = name.Replace("Windows 10", "Windows 11");   // в реестре Windows 11 по-прежнему «10»
            var version = key?.GetValue("DisplayVersion") as string;
            return name is null ? RuntimeInformation.OSDescription : version is null ? name : $"{name} {version}";
        }
        catch { return RuntimeInformation.OSDescription; }
    }

    private static List<DiskReport> Disks()
    {
        var list = new List<DiskReport>();
        try
        {
            foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
            {
                try
                {
                    if (!d.IsReady) continue;
                    list.Add(new(d.Name.TrimEnd('\\'), HardwareReport.ToGb(d.TotalSize), HardwareReport.ToGb(d.AvailableFreeSpace)));
                }
                catch (IOException) { /* диск отвалился во время опроса: пропускаем */ }
            }
        }
        catch { /* без дисков тоже можно */ }
        return list;
    }

    /// <summary>Адрес и MAC рабочего сетевого адаптера: проводной в приоритете, виртуальные (VPN, Hyper-V) не берём.</summary>
    internal static (string? Ip, string? Mac) Network()
    {
        if (NativeHardware.Network() is { } found) return (found.Ip, found.Mac);
        return NetworkManaged();
    }

    internal static (string? Ip, string? Mac) NetworkManaged()
    {
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211
                            && !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                            && !n.Description.Contains("VPN", StringComparison.OrdinalIgnoreCase)
                            && !n.Description.Contains("TAP", StringComparison.OrdinalIgnoreCase))
                .Select(n => (Nic: n, Ip: n.GetIPProperties().UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254"))?.Address))
                .Where(x => x.Ip is not null)
                .OrderBy(x => x.Nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 0 : 1)
                .ToList();
            var best = candidates.FirstOrDefault();
            return best.Nic is null ? (null, null) : (best.Ip!.ToString(), HardwareReport.FormatMac(best.Nic.GetPhysicalAddress().GetAddressBytes()));
        }
        catch { return (null, null); }
    }
}

/// <summary>Отправляет железо на сервер при запуске и затем раз в несколько часов. Сбой не опасен: повторим позже.</summary>
public sealed class HardwareReporter(ServerApi api, ILogger<HardwareReporter> log) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromHours(6);
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(10);

    /// <summary>Одна отправка. Возвращает true, если сервер принял отчёт.</summary>
    public static async Task<bool> ReportOnceAsync(ServerApi api, HardwareReport report, CancellationToken ct)
    {
        var r = await api.PostAsync<System.Text.Json.JsonElement>("/api/agent/hardware", report.Normalized(), null, ct).ConfigureAwait(false);
        return r.Ok;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ConfigureAwait(false);   // дать службе спокойно подняться и связаться с сервером
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!api.HasKey)                                                                // ПК ещё подключается по коду: ключа пока нет
            {
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                continue;
            }
            var delay = Every;
            try
            {
                var ok = await ReportOnceAsync(api, HardwareCollector.Collect(), stoppingToken).ConfigureAwait(false);
                if (!ok) { delay = RetryAfter; log.LogWarning("Не удалось отправить характеристики ПК, повторим через {Minutes} мин", RetryAfter.TotalMinutes); }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) { delay = RetryAfter; log.LogError(e, "Сбой при сборе характеристик ПК"); }
            try { await Task.Delay(delay, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }
}
