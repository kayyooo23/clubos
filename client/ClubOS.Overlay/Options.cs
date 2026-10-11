using ClubOS.Shared;

namespace ClubOS.Overlay;

/// <summary>
/// Параметры запуска. По умолчанию — безопасный режим разработки: обычное окно, ничего не блокируется.
///   --kiosk        полноэкранная блокировка поверх всего + перехват клавиш (для настоящих ПК гостей)
///   --allow-exit   в киоске разрешить аварийный выход Ctrl+Shift+F12 ×3 (на время проверки на живом ПК)
///   --pipe имя     имя канала службы (по умолчанию ClubOS.Agent или переменная CLUBOS_PIPE)
/// </summary>
public sealed record Options(bool Kiosk, bool AllowExit, string PipeName)
{
    public static Options Parse(string[] args)
    {
        var pipe = AgentConfig.PipeName;
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "--pipe") pipe = args[i + 1];
        return new Options(args.Contains("--kiosk"), args.Contains("--allow-exit"), pipe);
    }
}
