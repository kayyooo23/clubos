using System.Text.RegularExpressions;

namespace ClubOS.Shared.Ui;

/// <summary>Что именно запустить: файл или адрес (для Steam), без каких-либо аргументов от гостя.</summary>
public sealed record LaunchPlan(string FileName, string? WorkingDirectory, bool UseShellExecute);

/// <summary>
/// Превращает запись каталога в команду запуска. Проверяет её ещё раз на ПК, хотя сервер уже проверил:
/// клиент не должен запускать то, что не похоже на игру, даже если сервер (или канал до него) подменили.
/// </summary>
public static partial class AppLaunch
{
    [GeneratedRegex(@"^[1-9]\d{0,9}$")] private static partial Regex SteamId();
    [GeneratedRegex(@"^[A-Za-z]:\\[^<>:""/|?*\x00-\x1f]+\.exe$", RegexOptions.IgnoreCase)] private static partial Regex ExePath();

    /// <summary>План запуска или null, если запись выглядит небезопасной или неполной (тогда в error причина).</summary>
    public static LaunchPlan? Plan(AppInfo app, out string? error)
    {
        error = null;
        switch (app.LaunchKind)
        {
            case "steam" when SteamId().IsMatch(app.LaunchTarget):
                return new($"steam://rungameid/{app.LaunchTarget}", null, true);

            case "exe" when ExePath().IsMatch(app.LaunchTarget) && !HasOddSegments(app.LaunchTarget):
                var dir = app.LaunchTarget[..app.LaunchTarget.LastIndexOf('\\')];
                return new(app.LaunchTarget, dir, false);

            default:
                error = "Запись в меню настроена неверно. Сообщите администратору";
                return null;
        }
    }

    /// <summary>«C:\Games\..\x.exe», «C:\\x.exe»: пустые части и переходы вверх запрещены.</summary>
    private static bool HasOddSegments(string path) =>
        path.Split('\\').Skip(1).Any(p => p is "" or "." or "..");
}
