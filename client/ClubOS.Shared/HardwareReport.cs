namespace ClubOS.Shared;

public sealed record DiskReport(string Name, int TotalGb, int FreeGb);

/// <summary>
/// Что ПК рассказывает серверу о своём железе (процессор, память, видеокарта, диски, сеть). Ничего личного: только модель железа.
/// Всё необязательное: что определить не удалось, остаётся пустым.
/// </summary>
public sealed record HardwareReport(string? Cpu, int? RamGb, string? Gpu, string? Os, string? Ip, string? Mac,
    IReadOnlyList<DiskReport> Disks)
{
    // те же пределы, что проверяет сервер: слишком длинное значение он бы отклонил целиком
    public const int MaxText = 100, MaxCpuGpu = 120, MaxIp = 45, MaxDisks = 8, MaxDiskName = 16;

    public static string? Clip(string? value, int max)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        return value.Length <= max ? value : value[..max];
    }

    /// <summary>Байты сетевой карты → «00-1F-16-DD-4D-CB». Не шесть байт: null.</summary>
    public static string? FormatMac(byte[]? bytes) =>
        bytes is { Length: 6 } ? string.Join('-', bytes.Select(b => b.ToString("X2"))) : null;

    public static int ToGb(long bytes) => (int)Math.Clamp((long)Math.Round(bytes / 1073741824.0), 0, 1_000_000);

    /// <summary>Приводит отчёт к пределам сервера (обрезает лишнее, оставляет не больше 8 дисков).</summary>
    public HardwareReport Normalized() => this with
    {
        Cpu = Clip(Cpu, MaxCpuGpu), Gpu = Clip(Gpu, MaxCpuGpu), Os = Clip(Os, MaxText), Ip = Clip(Ip, MaxIp),
        Mac = Mac is { Length: 17 } ? Mac : null,
        Disks = Disks.Take(MaxDisks).Select(d => d with { Name = Clip(d.Name, MaxDiskName) ?? "?" }).ToList(),
    };
}
