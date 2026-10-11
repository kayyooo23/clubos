using Microsoft.Extensions.Logging;

namespace ClubOS.Service.Cleanup;

public enum CleanupMode { Off, DryRun, On }

public sealed record ProcessEntry(int Id, string Name, int SessionId, DateTime StartTime);

/// <summary>Кто сейчас вошёл в Windows на этом ПК (консольная сессия): имя, SID, папка профиля.</summary>
public sealed record ConsoleUser(string Name, string Sid, string ProfileDir, int SessionId);

/// <summary>Всё, что очистка берёт у системы. В боевой службе это Windows (WindowsSystemOps), в тестах подставка.</summary>
public interface ISystemOps
{
    ConsoleUser? GetConsoleUser();
    IReadOnlyList<ProcessEntry> ListProcesses();
    /// <summary>Закрывает процесс вместе с дочерними. True, если процесса больше нет.</summary>
    bool KillTree(int processId);
    /// <summary>Убирает сохранённый вход в Steam для пользователя (автовход и запомненный пароль).</summary>
    void ResetSteamLogin(ConsoleUser user, List<string> report);
}

public sealed record CleanupOptions(
    CleanupMode Mode,
    string? GuestUser,
    string ProfilesRoot,
    IReadOnlyList<string> ExtraPaths,
    IReadOnlyList<string> KeepProcesses)
{
    public static string DefaultProfilesRoot => Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "Users");
}

public sealed record CleanupReport(bool Ran, bool DryRun, string Reason, IReadOnlyList<string> Killed, IReadOnlyList<string> Deleted, IReadOnlyList<string> Errors)
{
    public static CleanupReport Skipped(string why) => new(false, false, why, [], [], []);
    public string Summary => !Ran ? $"Очистка пропущена: {Reason}"
        : $"{(DryRun ? "Пробная очистка (ничего не удалено)" : "Очистка ПК")}: {Reason}. Процессов: {Killed.Count}, путей: {Deleted.Count}, ошибок: {Errors.Count}";
}

/// <summary>
/// Чистит ПК после гостя: закрывает всё, что он запустил, и убирает его данные (браузеры, Steam и другие входы, файлы).
/// Принципы безопасности:
///  • действует ТОЛЬКО в сессии Windows-пользователя GuestUser; если вошёл кто-то другой (администратор), не делает ничего;
///  • закрывает процессы, которых не было при «базовом снимке» (снимок делается, когда ПК чистый), и никогда не трогает системные;
///  • удаляет только внутри профиля этого пользователя, ссылки и точки соединения не разворачивает;
///  • в режиме dry-run только пишет, что сделал бы.
/// </summary>
public sealed class SessionCleaner(ISystemOps ops, CleanupOptions options, ILogger<SessionCleaner>? log = null, TimeSpan? settle = null)
{
    private readonly TimeSpan _settle = settle ?? TimeSpan.FromMilliseconds(1500);   // пауза между проходами закрытия процессов
    /// <summary>Имена процессов (без .exe, строчными), которые не закрываются никогда: оболочка Windows, служебное, сам ClubOS.</summary>
    public static readonly IReadOnlySet<string> AlwaysKeep = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "winlogon", "csrss", "wininit", "services", "lsass", "smss", "svchost", "sihost", "ctfmon", "fontdrvhost",
        "taskhostw", "runtimebroker", "shellexperiencehost", "startmenuexperiencehost", "searchhost", "searchapp", "textinputhost",
        "applicationframehost", "conhost", "dllhost", "audiodg", "securityhealthsystray", "backgroundtaskhost", "system", "registry",
        "idle", "memory compression", "wudfhost", "userinit", "lockapp", "logonui",
        "ClubOS.Overlay", "ClubOS.Service",
    };

    /// <summary>Известные программы гостей: закрываются, даже если базового снимка нет (например, служба перезапустилась посреди дня).</summary>
    public static readonly IReadOnlySet<string> KnownGuestApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "steam", "steamwebhelper", "chrome", "msedge", "firefox", "opera", "browser", "brave", "discord", "telegram", "whatsapp",
        "epicgameslauncher", "epicwebhelper", "battle.net", "agent", "riotclientservices", "riotclientux", "leagueclient", "valorant",
        "origin", "eadesktop", "eaconnect", "uplay", "ubisoftconnect", "gog galaxy", "galaxyclient", "spotify", "skype", "teams", "zoom",
        "obs64", "obs32", "dota2", "cs2", "csgo", "pubg", "tslgame", "gta5", "fortniteclient-win64-shipping",
    };

    /// <summary>Что удаляется в профиле гостя. Tree: папка целиком; Contents: содержимое, сама папка остаётся; Desktop: всё, кроме ярлыков.</summary>
    public enum Kind { Tree, Contents, DesktopFiles }

    public static readonly IReadOnlyList<(string Path, Kind Kind)> DefaultTargets =
    [
        (@"AppData\Local\Google\Chrome\User Data", Kind.Tree),
        (@"AppData\Local\Microsoft\Edge\User Data", Kind.Tree),
        (@"AppData\Roaming\Mozilla\Firefox\Profiles", Kind.Tree),
        (@"AppData\Local\Yandex\YandexBrowser\User Data", Kind.Tree),
        (@"AppData\Local\BraveSoftware\Brave-Browser\User Data", Kind.Tree),
        (@"AppData\Roaming\Opera Software", Kind.Tree),
        (@"AppData\Roaming\discord", Kind.Tree),
        (@"AppData\Roaming\Telegram Desktop\tdata", Kind.Tree),
        (@"AppData\Local\EpicGamesLauncher\Saved\Config", Kind.Tree),
        (@"AppData\Local\EpicGamesLauncher\Saved\webcache", Kind.Tree),
        (@"AppData\Roaming\Battle.net", Kind.Tree),
        (@"AppData\Local\Battle.net", Kind.Tree),
        (@"AppData\Local\Riot Games\Riot Client\Data", Kind.Tree),
        (@"AppData\Local\Ubisoft Game Launcher", Kind.Tree),
        (@"AppData\Roaming\Microsoft\Windows\Recent", Kind.Contents),
        (@"AppData\Local\Temp", Kind.Contents),
        ("Downloads", Kind.Contents), ("Documents", Kind.Contents), ("Pictures", Kind.Contents), ("Videos", Kind.Contents), ("Music", Kind.Contents),
        ("Desktop", Kind.DesktopFiles),
    ];

    private readonly object _gate = new();
    private HashSet<(int Id, long Started)>? _baseline;

    public bool HasBaseline { get { lock (_gate) return _baseline is not null; } }

    /// <summary>Запоминает, какие процессы уже были в сессии гостя, когда ПК чистый. Их очистка не закрывает.</summary>
    public bool TakeBaseline()
    {
        var user = GuestSession(out _);
        if (user is null) return false;
        var set = ops.ListProcesses().Where(p => p.SessionId == user.SessionId).Select(p => (p.Id, p.StartTime.Ticks)).ToHashSet();
        lock (_gate) _baseline = set;
        log?.LogInformation("Базовый снимок процессов: {Count}", set.Count);
        return true;
    }

    /// <summary>Сессия Windows, в которой разрешена очистка: только консольная и только гостевого пользователя.</summary>
    private ConsoleUser? GuestSession(out string why)
    {
        why = "";
        if (options.Mode == CleanupMode.Off) { why = "очистка выключена"; return null; }
        if (string.IsNullOrWhiteSpace(options.GuestUser)) { why = "не задан guest_user"; return null; }
        var user = ops.GetConsoleUser();
        if (user is null) { why = "в Windows никто не вошёл"; return null; }
        if (!string.Equals(user.Name, options.GuestUser, StringComparison.OrdinalIgnoreCase))
        {
            why = $"в Windows вошёл «{user.Name}», а гостевой пользователь «{options.GuestUser}»";
            return null;
        }
        return user;
    }

    public CleanupReport Clean(string reason)
    {
        var user = GuestSession(out var why);
        if (user is null) return CleanupReport.Skipped(why);
        var dry = options.Mode == CleanupMode.DryRun;
        var killed = new List<string>();
        var deleted = new List<string>();
        var errors = new List<string>();

        KillGuestProcesses(user, dry, killed, errors);
        if (ProfileIsSafe(user, errors))
            foreach (var (path, kind) in Targets()) DeleteTarget(user, path, kind, dry, deleted, errors);
        if (!dry) ops.ResetSteamLogin(user, errors);
        if (!dry) TakeBaseline();   // после уборки ПК чистый: это новая точка отсчёта

        var report = new CleanupReport(true, dry, reason, killed, deleted, errors);
        log?.LogInformation("{Summary}", report.Summary);
        foreach (var name in killed.Distinct().Take(30)) log?.LogInformation("  закрыто: {Name}", name);
        foreach (var path in deleted.Take(40)) log?.LogInformation("  удалено: {Path}", path);
        foreach (var e in errors.Take(20)) log?.LogWarning("  не вышло: {Error}", e);
        return report;
    }

    // ---------- процессы ----------
    private bool Protected(string name) =>
        AlwaysKeep.Contains(name) || options.KeepProcesses.Contains(name, StringComparer.OrdinalIgnoreCase);

    private void KillGuestProcesses(ConsoleUser user, bool dry, List<string> killed, List<string> errors)
    {
        HashSet<(int, long)>? baseline;
        lock (_gate) baseline = _baseline;
        for (var round = 0; round < 3; round++)   // загрузчики перезапускают друг друга: после первого прохода смотрим ещё раз
        {
            var victims = ops.ListProcesses()
                .Where(p => p.SessionId == user.SessionId && p.Id != Environment.ProcessId && !Protected(p.Name))
                .Where(p => baseline is not null ? !baseline.Contains((p.Id, p.StartTime.Ticks)) : KnownGuestApps.Contains(p.Name))
                .ToList();
            if (victims.Count == 0) return;
            foreach (var p in victims)
            {
                if (dry) killed.Add($"{p.Name} ({p.Id})");
                else if (ops.KillTree(p.Id)) killed.Add($"{p.Name} ({p.Id})");
                else errors.Add($"процесс {p.Name} ({p.Id}) не закрылся");
            }
            if (dry) return;
            if (_settle > TimeSpan.Zero) Thread.Sleep(_settle);
        }
    }

    // ---------- файлы ----------
    private IEnumerable<(string Path, Kind Kind)> Targets() =>
        DefaultTargets.Concat(options.ExtraPaths.Select(p => (p, Kind.Tree)));

    /// <summary>Полный путь внутри профиля или null, если путь пытается выйти за его пределы.</summary>
    public static string? ResolveInside(string profileDir, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':')) return null;
        var root = Path.GetFullPath(profileDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && full.Length > root.Length ? full : null;
    }

    private bool ProfileIsSafe(ConsoleUser user, List<string> errors)
    {
        // профиль должен лежать прямо в C:\Users (или в заданной корневой папке), иначе ничего не удаляем
        try
        {
            var profile = Path.GetFullPath(user.ProfileDir).TrimEnd(Path.DirectorySeparatorChar);
            var root = Path.GetFullPath(options.ProfilesRoot).TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(Path.GetDirectoryName(profile), root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(profile)) return true;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { }
        errors.Add($"профиль «{user.ProfileDir}» лежит не в {options.ProfilesRoot}: файлы не трогаем");
        return false;
    }

    private void DeleteTarget(ConsoleUser user, string relative, Kind kind, bool dry, List<string> deleted, List<string> errors)
    {
        var full = ResolveInside(user.ProfileDir, relative);
        if (full is null) { errors.Add($"путь «{relative}» выходит за пределы профиля, пропущен"); return; }
        if (!File.Exists(full) && !Directory.Exists(full)) return;
        try
        {
            switch (kind)
            {
                case Kind.Tree:
                    deleted.Add(relative);
                    if (!dry) DeleteEntry(full, errors);
                    break;
                case Kind.Contents:
                    foreach (var child in SafeChildren(full)) { deleted.Add(Path.Combine(relative, Path.GetFileName(child))); if (!dry) DeleteEntry(child, errors); }
                    break;
                case Kind.DesktopFiles:
                    foreach (var child in SafeChildren(full).Where(c => !IsShortcut(c)))
                    { deleted.Add(Path.Combine(relative, Path.GetFileName(child))); if (!dry) DeleteEntry(child, errors); }
                    break;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { errors.Add($"{relative}: {e.Message}"); }
    }

    private static bool IsShortcut(string path) =>
        path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".url", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetFileName(path), "desktop.ini", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> SafeChildren(string dir)
    {
        try { return Directory.EnumerateFileSystemEntries(dir).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>Удаляет файл или папку. Ссылки (symlink, junction) удаляются сами, а то, на что они указывают, не трогается.</summary>
    public static void DeleteEntry(string path, List<string> errors)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReparsePoint) != 0)
            {
                if ((attrs & FileAttributes.Directory) != 0) Directory.Delete(path, false); else File.Delete(path);
                return;
            }
            if ((attrs & FileAttributes.Directory) != 0)
            {
                foreach (var child in SafeChildren(path)) DeleteEntry(child, errors);
                Directory.Delete(path, false);
            }
            else
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { errors.Add($"{Path.GetFileName(path)}: {e.Message}"); }
    }
}
