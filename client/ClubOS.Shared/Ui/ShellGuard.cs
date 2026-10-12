namespace ClubOS.Shared.Ui;

/// <summary>Окно верхнего уровня глазами ShellGuard. Список окон собирает оверлей из Win32 (сверху вниз по порядку на экране), здесь только решения.</summary>
public sealed record WindowInfo(long Handle, int ProcessId, string ProcessName, string ClassName, int Width, int Height, bool Minimized = false);

public enum GuardAction
{
    None,
    ShowMenu,       // вернуть меню ClubOS на передний план
    PullToFront,    // вывести на передний план только что запущенную игру (Handle)
    CloseWindow,    // закрыть окно Проводника с папками (Handle)
}

public readonly record struct GuardDecision(GuardAction Action, long Handle = 0)
{
    public static GuardDecision Nothing { get; } = new(GuardAction.None);
}

/// <summary>
/// Пока идёт сессия гостя, меню ClubOS занимает весь экран и не сворачивается; всё, что гость запускает, открывается поверх него, а когда
/// игра закрыта, под ней снова меню. ShellGuard решает, что делать с окнами, чтобы гость не оказался на рабочем столе Windows:
///  • окно Проводника с папками закрывается сразу;
///  • на переднем плане рабочий стол, панель задач или меню «Пуск» (или ничего): возвращаем меню;
///  • гость только что запустил игру (ожидание LaunchGrace), а на переднем плане ещё меню: игра могла открыться позади (Windows не всегда отдаёт ей фокус),
///    выводим её окно вперёд. Окна, которые были до запуска, не трогаем;
///  • на переднем плане игра или программа: не мешаем.
/// Не знает про Win32: тест проверяет только решения.
/// </summary>
public sealed class ShellGuard
{
    /// <summary>Сколько после запуска игры мы вытаскиваем её окно вперёд, если оно открылось позади меню. Игры и лаунчеры грузятся долго.</summary>
    public static readonly TimeSpan LaunchGrace = TimeSpan.FromSeconds(90);

    private const int MinWidth = 200, MinHeight = 120;   // всплывашки и невидимые вспомогательные окна выводить вперёд не нужно
    private static readonly string[] ShellProcesses = ["explorer", "StartMenuExperienceHost", "SearchHost", "SearchApp", "ShellExperienceHost"];
    private static readonly string[] ExplorerWindowClasses = ["CabinetWClass", "ExploreWClass"];

    private DateTimeOffset? _graceUntil;
    private HashSet<long> _before = [];

    public bool InGrace(DateTimeOffset now) => _graceUntil is { } until && now < until;

    /// <summary>Гость запустил игру. existing: окна, которые уже были на экране (их позже не вытаскиваем).</summary>
    public void Launched(DateTimeOffset now, IEnumerable<long> existing)
    {
        _graceUntil = now + LaunchGrace;
        _before = [.. existing];
    }

    /// <summary>Сессия кончилась или началась заново: забываем запуски.</summary>
    public void Reset()
    {
        _graceUntil = null;
        _before = [];
    }

    private static bool IsShell(WindowInfo w) => ShellProcesses.Contains(w.ProcessName, StringComparer.OrdinalIgnoreCase);

    private static bool IsExplorerWindow(WindowInfo w) =>
        w.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase) && ExplorerWindowClasses.Contains(w.ClassName);

    /// <summary>foreground: окно в фокусе (null, если никакого). windows: видимые окна верхнего уровня, самое верхнее первым. ownPid: процесс оверлея ClubOS.</summary>
    public GuardDecision Decide(WindowInfo? foreground, IReadOnlyList<WindowInfo> windows, int ownPid, DateTimeOffset now)
    {
        var explorer = windows.FirstOrDefault(w => !w.Minimized && IsExplorerWindow(w));
        if (explorer is not null) return new(GuardAction.CloseWindow, explorer.Handle);

        if (!InGrace(now)) _graceUntil = null;

        if (foreground is not null && foreground.ProcessId != ownPid && !IsShell(foreground))
        {
            _graceUntil = null;               // игра или программа сама вышла вперёд: вытаскивать больше нечего
            return GuardDecision.Nothing;
        }
        if (foreground is null || IsShell(foreground)) return new(GuardAction.ShowMenu);

        if (_graceUntil is not null)
        {
            var launched = windows.FirstOrDefault(w =>
                w.ProcessId != ownPid && !IsShell(w) && !w.Minimized && !_before.Contains(w.Handle) && w.Width >= MinWidth && w.Height >= MinHeight);
            if (launched is not null) return new(GuardAction.PullToFront, launched.Handle);
        }
        return GuardDecision.Nothing;
    }
}
