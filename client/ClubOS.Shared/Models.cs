using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClubOS.Shared;

/// <summary>Режим ПК, который сервер возвращает в /api/agent/state.</summary>
[JsonConverter(typeof(PcModeConverter))]
public enum PcMode
{
    Locked,        // экран блокировки (если есть Account — гость в кабинете)
    GuestSession,  // идёт игровая сессия, ПК разблокирован
    StaffDesktop,  // вошёл сотрудник: обычный рабочий стол
}

/// <summary>Сервер шлёт режимы как locked / guest_session / staff_desktop.</summary>
public sealed class PcModeConverter : JsonStringEnumConverter<PcMode>
{
    public PcModeConverter() : base(JsonNamingPolicy.SnakeCaseLower) { }
}

public sealed record AccountInfo(int? Id, string Role, string? PhoneMasked, int? Balance);

public sealed record SessionInfo(
    int Id, int PcId, int AccountId, string Tariff,
    DateTime StartedAt, DateTime? PlannedEnd, DateTime? EndedAt, int Cost, int? SecondsLeft,
    string? Kind = null, bool Overtime = false, int? OvertimePrice = null);

public sealed record TariffInfo(int Id, string Name, string Kind, int Price, int? PackageMinutes, int? NightStart, int? NightEnd);

/// <summary>Игра или программа из меню гостя (каталог хранит сервер). LaunchKind: steam (LaunchTarget = номер игры) или exe (полный путь).</summary>
public sealed record AppInfo(int Id, string Title, string Tab, string Category, int? AgeRating, string LaunchKind, string LaunchTarget,
    string? CoverVersion = null, string? CoverPath = null);
// CoverVersion: отпечаток обложки на сервере (null: обложки нет). CoverPath: файл, который служба уже скачала на ПК (с сервера не приходит).

/// <summary>
/// Рекламный баннер для экрана входа. Advertiser и Erid нужны для маркировки: под баннером показывается «РЕКЛАМА», рекламодатель и erid.
/// Seconds: сколько баннер на экране, пока сменяются несколько. Version: отпечаток картинки (меняется при замене файла).
/// Path: локальный файл, который служба скачала и проверила; с сервера не приходит.
/// </summary>
public sealed record AdInfo(int Id, string? Advertiser, string? Erid, int Seconds, int Width, int Height, string Version, string? Path = null);

/// <summary>Товар в баре клуба.</summary>
public sealed record ProductInfo(int Id, string Name, string Category, int Price);

public sealed record OrderLine(string Name, int Price, int Qty);

/// <summary>Заказ гостя. Status: new (ждёт сотрудника), accepted (готовят), done (выдан), cancelled (отменён, деньги возвращены).</summary>
public sealed record OrderInfo(int Id, int Total, string Status, string? Comment, DateTime CreatedAt, IReadOnlyList<OrderLine> Items);

/// <summary>Вызов администратора с этого ПК.</summary>
public sealed record CallInfo(int Id, string Status, string? Text);

/// <summary>Одна строка корзины в запросе на заказ.</summary>
public sealed record CartLine(int ProductId, int Qty);

public sealed record TransactionInfo(int Id, int Amount, int BalanceAfter, string Kind, DateTime CreatedAt);

/// <summary>Сообщение администратора гостю на экран (любой текст, например «Пожалуйста, постарайтесь вести себя потише»). Id нужен, чтобы показать его один раз.</summary>
public sealed record NoticeInfo(int Id, string Text);

/// <summary>Параметры команды ПК. Force: администратор подтвердил, что команду нужно выполнить даже при идущей сессии гостя.</summary>
public sealed record CommandPayload(bool Force = false);

/// <summary>Команда администратору для этого ПК (reboot, shutdown). Служба выполняет её сама и присылает результат; оверлей о командах не знает.</summary>
public sealed record CommandInfo(int Id, string Kind, CommandPayload? Payload = null);

/// <summary>Другой участник синхронизации, у которого можно брать файлы: адрес в локальной сети и роль (source: эталон, target: уже получивший).</summary>
public sealed record SyncPeer(int PcId, string Url, string Role);

/// <summary>
/// Часть этого ПК в задании синхронизации игр. Role: source (эталон: считает список файлов и раздаёт) или target (получатель). Status: что сервер знает о нас.
/// ManifestSha256 пусто, пока эталон не посчитал список файлов. Peers: у кого сейчас можно брать файлы (только для получателя, когда список готов).
/// </summary>
public sealed record SyncAssignment(int Id, string Role, string Status, string Token, IReadOnlyList<string> Paths, bool DeleteExtra,
    string? ManifestSha256 = null, IReadOnlyList<SyncPeer>? Peers = null);

/// <summary>
/// Ответ сервера на /api/agent/state. Notices: свежие сообщения администратора для этого ПК (сервер повторяет их пару минут, показывает клиент один раз).
/// Commands: команды, которые ждут выполнения на этом ПК (сервер повторяет неподтверждённую команду, служба защищена от повторного выполнения).
/// </summary>
public sealed record ServerState(PcMode Mode, AccountInfo? Account, SessionInfo? Session, string? Message,
    string? ClubName = null, string? PcName = null, bool? ScanPrograms = null, IReadOnlyList<NoticeInfo>? Notices = null,
    IReadOnlyList<CommandInfo>? Commands = null, bool? ScanGames = null, IReadOnlyList<SyncAssignment>? Sync = null);

/// <summary>
/// Что служба показывает оверлею. Собирается на каждый запрос «state», поэтому SecondsLeft всегда свежий.
/// Connected=false — сервер недоступен (показываем последнее известное состояние, пока не истёк запас).
/// </summary>
public sealed record AgentSnapshot(
    bool Connected, PcMode Mode, AccountInfo? Account, SessionInfo? Session, string? Message, int? SecondsLeft,
    string? ClubName = null, string? PcName = null, IReadOnlyList<NoticeInfo>? Notices = null)
{
    /// <summary>Состояние до первого ответа сервера: ПК закрыт, связи нет.</summary>
    public static AgentSnapshot Booting { get; } = new(false, PcMode.Locked, null, null, null, null);
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
