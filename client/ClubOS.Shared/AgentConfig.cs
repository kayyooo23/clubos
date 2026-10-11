using System.Text.Json;

namespace ClubOS.Shared;

/// <summary>
/// Настройки службы. В проде файл лежит в %ProgramData%\ClubOS\agent.json и доступен только системе и администраторам
/// (права выставляет скрипт установки): гость не должен прочитать ключ ПК.
/// </summary>
public sealed record AgentConfig(string Server, string PcKey, int OfflineGraceSeconds = 120, int PollSeconds = 2,
    string Cleanup = "dry-run", string? GuestUser = null, IReadOnlyList<string>? CleanupPaths = null, IReadOnlyList<string>? CleanupKeep = null)
{
    // Очистка ПК после сессии гостя (см. ClubOS.Service/Cleanup):
    //   Cleanup      off | dry-run | on. dry-run только записывает в журнал, что было бы закрыто и удалено.
    //   GuestUser    имя Windows-пользователя, под которым играют гости. Очистка действует ТОЛЬКО в его сессии.
    //   CleanupPaths дополнительные папки в профиле гостя, которые нужно удалять (относительно профиля, например "AppData\Roaming\MyLauncher").
    //   CleanupKeep  имена процессов (без .exe), которые никогда не закрывать.
    public const string DefaultPipeName = "ClubOS.Agent";

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ClubOS", "agent.json");

    /// <summary>Порядок: переменные окружения CLUBOS_SERVER / CLUBOS_PC_KEY (для разработки), затем файл.</summary>
    public static AgentConfig Load(string? path = null)
    {
        path ??= Environment.GetEnvironmentVariable("CLUBOS_CONFIG") ?? DefaultPath;
        AgentConfig? fromFile = null;
        if (File.Exists(path))
            fromFile = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path), Json.Options);

        var server = Environment.GetEnvironmentVariable("CLUBOS_SERVER") ?? fromFile?.Server;
        var key = Environment.GetEnvironmentVariable("CLUBOS_PC_KEY") ?? fromFile?.PcKey;
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException(
                $"Не заданы адрес сервера и ключ ПК. Создайте {path} (см. client/README.md) или задайте CLUBOS_SERVER и CLUBOS_PC_KEY.");
        var cleanup = (Environment.GetEnvironmentVariable("CLUBOS_CLEANUP") ?? fromFile?.Cleanup ?? "dry-run").Trim().ToLowerInvariant();
        if (cleanup is not ("off" or "dry-run" or "on"))
            throw new InvalidOperationException($"Параметр cleanup может быть off, dry-run или on, а не «{cleanup}».");
        return new AgentConfig(server.TrimEnd('/'), key, fromFile?.OfflineGraceSeconds ?? 120, fromFile?.PollSeconds ?? 2,
            cleanup, Environment.GetEnvironmentVariable("CLUBOS_GUEST_USER") ?? fromFile?.GuestUser, fromFile?.CleanupPaths, fromFile?.CleanupKeep);
    }

    /// <summary>Папка скачанных обложек игр (как AdsDir: служба пишет, оверлей читает). Для разработки CLUBOS_COVERS_DIR.</summary>
    public static string CoversDir => Environment.GetEnvironmentVariable("CLUBOS_COVERS_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ClubOS", "covers");

    public static string PipeName => Environment.GetEnvironmentVariable("CLUBOS_PIPE") ?? DefaultPipeName;

    /// <summary>
    /// Папка, куда служба кладёт скачанные рекламные баннеры. Служба пишет, оверлей (от имени гостя) только читает, поэтому папка вне
    /// закрытой части ClubOS: ключ ПК рядом не лежит. Для разработки меняется переменной CLUBOS_ADS_DIR.
    /// </summary>
    public static string AdsDir => Environment.GetEnvironmentVariable("CLUBOS_ADS_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ClubOS", "ads");
}
