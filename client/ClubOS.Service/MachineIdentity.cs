using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace ClubOS.Service;

/// <summary>
/// Отпечаток компьютера: по нему сервер узнаёт тот же ПК при повторном подключении и замечает копию диска на другом железе.
/// Берётся то, что прошито в самом компьютере: UUID и серийный номер материнской платы из таблицы SMBIOS. Они не меняются при переустановке
/// Windows и разные у разных плат, в отличие от MachineGuid, который копируется вместе с образом диска. У дешёвых плат бывают одинаковые
/// заводские заглушки («To be filled by O.E.M.»): их мы не считаем. Если ничего пригодного нет, остаётся MachineGuid (клон тогда не заметить, но ПК работает).
/// Сам отпечаток не секрет: это хеш, а не серийные номера; защищает ключ ПК, а не заменяет его.
/// </summary>
public static class MachineIdentity
{
    private static readonly HashSet<string> PlaceholderTexts = new(StringComparer.OrdinalIgnoreCase)
    {
        "to be filled by o.e.m.", "default string", "none", "not specified", "not applicable", "n/a", "unknown", "system serial number",
        "base board serial number", "serial number", "type2 - board serial number", "123456789", "o.e.m.", "oem",
    };

    private static readonly HashSet<string> PlaceholderUuids = new(StringComparer.OrdinalIgnoreCase)
    {
        "00000000-0000-0000-0000-000000000000", "ffffffff-ffff-ffff-ffff-ffffffffffff", "03000200-0400-0500-0006-000700080009",
    };

    public static bool UsableSerial(string? value)
    {
        var v = value?.Trim();
        return !string.IsNullOrEmpty(v) && v.Length >= 4 && !PlaceholderTexts.Contains(v) && v.Distinct().Count() > 1;   // «0000000», «xxxxxxx»: тоже заглушки
    }

    public static bool UsableUuid(string? value) => Guid.TryParse(value, out _) && !PlaceholderUuids.Contains(value!.Trim());

    /// <summary>Чистая функция: из того, что нашли в компьютере, получается отпечаток (32 символа). Одно и то же железо даёт один и тот же результат.</summary>
    public static string Compute(string? smbiosUuid, string? boardSerial, string? machineGuid)
    {
        var parts = new List<string>();
        if (UsableUuid(smbiosUuid)) parts.Add("uuid:" + smbiosUuid!.Trim().ToLowerInvariant());
        if (UsableSerial(boardSerial)) parts.Add("board:" + boardSerial!.Trim());
        var basis = parts.Count > 0 ? "hw|" + string.Join('|', parts) : "os|" + (string.IsNullOrWhiteSpace(machineGuid) ? Environment.MachineName : machineGuid.Trim());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("clubos-machine-v1|" + basis)))[..32].ToLowerInvariant();
    }

    private static string? _cached;

    /// <summary>Отпечаток этого компьютера. Каждая часть защищена: не определилась (нет прав, нестандартная Windows) — берётся следующая.</summary>
    public static string Detect()
    {
        if (_cached is not null) return _cached;
        string? uuid = null, board = null;
        try
        {
            if (Smbios.ReadRaw() is { } raw) (uuid, board) = Smbios.Parse(raw);
        }
        catch { /* оставляем пустым: ниже запасной вариант */ }
        return _cached = Compute(uuid, board, MachineGuid());
    }

    private static string? MachineGuid()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string;
        }
        catch { return null; }
    }
}

/// <summary>Чтение таблицы SMBIOS, которую прошивка отдаёт Windows. Нужны только два поля: UUID системы и серийный номер платы.</summary>
internal static class Smbios
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint signature, uint tableId, byte[]? buffer, uint size);

    private const uint Rsmb = 0x52534D42;   // 'RSMB'

    public static byte[]? ReadRaw()
    {
        var size = GetSystemFirmwareTable(Rsmb, 0, null, 0);
        if (size is 0 or > 1 << 20) return null;
        var buffer = new byte[size];
        return GetSystemFirmwareTable(Rsmb, 0, buffer, size) == size ? buffer : null;
    }

    /// <summary>Разбор: 8 байт заголовка (последние 4 — длина таблицы), затем записи «тип, длина, дескриптор, поля, строки».</summary>
    public static (string? Uuid, string? BoardSerial) Parse(byte[] raw)
    {
        if (raw.Length < 8) return (null, null);
        var end = (int)Math.Min((long)raw.Length, 8L + BitConverter.ToUInt32(raw, 4));
        string? uuid = null, board = null;
        var pos = 8;
        while (pos + 4 <= end)
        {
            var type = raw[pos];
            var formatted = raw[pos + 1];
            if (formatted < 4 || pos + formatted > end) break;
            var strings = new List<string>();
            var p = pos + formatted;
            if (p + 1 < end && raw[p] == 0 && raw[p + 1] == 0) p += 2;           // строк нет: два нуля подряд
            else
            {
                while (p < end && raw[p] != 0)
                {
                    var start = p;
                    while (p < end && raw[p] != 0) p++;
                    strings.Add(Encoding.ASCII.GetString(raw, start, p - start));
                    p++;                                                           // ноль после строки
                }
                p++;                                                               // замыкающий ноль набора строк
            }
            string? Text(int index) => index >= 1 && index <= strings.Count ? strings[index - 1] : null;
            if (type == 1 && formatted >= 0x18)                                    // сведения о системе: UUID в байтах 8..23, серийный номер — строка с номером в байте 7
                uuid ??= new Guid(raw.AsSpan(pos + 8, 16)).ToString();
            else if (type == 2 && formatted >= 8)                                  // материнская плата: серийный номер — строка с номером в байте 7
                board ??= Text(raw[pos + 7]);
            if (type == 127) break;                                                // конец таблицы
            pos = p;
        }
        return (uuid, board);
    }
}
