using System.Text.Json;

namespace ClubOS.Shared;

/// <summary>
/// Настройки службы. В проде файл лежит в %ProgramData%\ClubOS\agent.json и доступен только системе и администраторам
/// (права выставляет скрипт установки): гость не должен прочитать ключ ПК.
/// </summary>
public sealed record AgentConfig(string Server, string? PcKey, int OfflineGraceSeconds = 120, int PollSeconds = 2,
    string Cleanup = "dry-run", string? GuestUser = null, IReadOnlyList<string>? CleanupPaths = null, IReadOnlyList<string>? CleanupKeep = null,
    string? EnrollCode = null, string? GamesRoot = null, int SyncPort = 47900)
{
    // Синхронизация игр: GamesRoot игровая папка этого ПК (например D:\Games или библиотека Steam), SyncPort порт, на котором ПК раздаёт файлы другим ПК клуба.
    // Нет GamesRoot: ПК в синхронизации игр не участвует.
    // Как ПК узнаёт свой ключ: PcKey (вписан руками, разработка и старые установки) или EnrollCode (код подключения из админки: при первом запуске
    // служба сама подключается к клубу, получает ключ и хранит его зашифрованным). Нет ни того ни другого, а ключ уже получен ранее: берётся сохранённый.
    // Очистка ПК после сессии гостя (см. ClubOS.Service/Cleanup):
    //   Cleanup      off | dry-run | on. dry-run только записывает в журнал, что было бы закрыто и удалено.
    //   GuestUser    имя Windows-пользователя, под которым играют гости. Очистка действует ТОЛЬКО в его сессии.
    //   CleanupPaths дополнительные папки в профиле гостя, которые нужно удалять (относительно профиля, например "AppData\Roaming\MyLauncher").
    //   CleanupKeep  имена процессов (без .exe), которые никогда не закрывать.
    public const string DefaultPipeName = "ClubOS.Agent";

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ClubOS", "agent.json");

    /// <summary>Порядок: переменные окружения CLUBOS_SERVER / CLUBOS_PC_KEY / CLUBOS_ENROLL_CODE (для разработки), затем файл.</summary>
    public static AgentConfig Load(string? path = null)
    {
        path ??= Environment.GetEnvironmentVariable("CLUBOS_CONFIG") ?? DefaultPath;
        AgentConfig? fromFile = null;
        if (File.Exists(path))
            fromFile = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path), Json.Options);

        var server = Environment.GetEnvironmentVariable("CLUBOS_SERVER") ?? fromFile?.Server;
        var key = Blank(Environment.GetEnvironmentVariable("CLUBOS_PC_KEY")) ?? Blank(fromFile?.PcKey);
        var code = Blank(Environment.GetEnvironmentVariable("CLUBOS_ENROLL_CODE")) ?? Blank(fromFile?.EnrollCode);
        if (string.IsNullOrWhiteSpace(server))
            throw new InvalidOperationException(
                $"Не задан адрес сервера. Создайте {path} (см. client/README.md) или задайте CLUBOS_SERVER.");
        var cleanup = (Environment.GetEnvironmentVariable("CLUBOS_CLEANUP") ?? fromFile?.Cleanup ?? "dry-run").Trim().ToLowerInvariant();
        if (cleanup is not ("off" or "dry-run" or "on"))
            throw new InvalidOperationException($"Параметр cleanup может быть off, dry-run или on, а не «{cleanup}».");
        return new AgentConfig(server.TrimEnd('/'), key, fromFile?.OfflineGraceSeconds ?? 120, fromFile?.PollSeconds ?? 2,
            cleanup, Environment.GetEnvironmentVariable("CLUBOS_GUEST_USER") ?? fromFile?.GuestUser, fromFile?.CleanupPaths, fromFile?.CleanupKeep, code,
            Blank(Environment.GetEnvironmentVariable("CLUBOS_GAMES_ROOT")) ?? Blank(fromFile?.GamesRoot),
            int.TryParse(Environment.GetEnvironmentVariable("CLUBOS_SYNC_PORT"), out var port) && port is > 0 and < 65536 ? port : fromFile?.SyncPort is > 0 and < 65536 ? fromFile.SyncPort : 47900);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Файл с ключом, который служба получила при подключении по коду (зашифрован Windows DPAPI). Для разработки CLUBOS_IDENTITY_FILE.</summary>
    public static string IdentityPath => Environment.GetEnvironmentVariable("CLUBOS_IDENTITY_FILE")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ClubOS", "identity.bin");

    /// <summary>Папка автообновления: состояние, список плохих версий, отметка «новая версия работает», задание помощнику, скачанный пакет. Для разработки CLUBOS_UPDATES_DIR.</summary>
    public static string UpdatesDir => Environment.GetEnvironmentVariable("CLUBOS_UPDATES_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ClubOS", "updates");

    /// <summary>Папка служебных файлов синхронизации (кэш списка файлов). Для разработки CLUBOS_SYNC_DIR.</summary>
    public static string SyncDir => Environment.GetEnvironmentVariable("CLUBOS_SYNC_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ClubOS", "sync");

    /// <summary>Журнал уже выполненных команд сервера (чтобы после перезагрузки та же команда не выполнилась второй раз). Для разработки CLUBOS_COMMANDS_FILE.</summary>
    public static string CommandJournalPath => Environment.GetEnvironmentVariable("CLUBOS_COMMANDS_FILE")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ClubOS", "commands.log");

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
