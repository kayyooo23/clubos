using System.Runtime.InteropServices;

namespace ClubOS.Shared.Native;

/// <summary>
/// Нативная библиотека клиента clubos_native.dll (C++20, папка client/ClubOS.Native): подсчёт хешей блоков для синхронизации игр, перехват клавиш киоска,
/// опрос окон для оболочки гостя, сбор характеристик ПК. Каждая обёртка этой папки сначала пробует нативный путь, а если библиотеки нет рядом с программой
/// (например, на ПК разработчика нет компилятора C++), работает прежний код на C#: результат тот же, это проверяют тесты.
/// Переменная окружения CLUBOS_NATIVE=0 отключает нативный путь целиком.
/// </summary>
public static class ClubosNative
{
    /// <summary>Версия интерфейса: как CLUBOS_ABI_VERSION в include/clubos_native.h. Библиотека другой версии не используется.</summary>
    public const int AbiVersion = 2;

    private static readonly Lazy<bool> Available = new(Probe);

    /// <summary>Библиотека нашлась, загрузилась и её интерфейс той же версии.</summary>
    public static bool IsAvailable => Available.Value;

    private static bool Probe()
    {
        if (Environment.GetEnvironmentVariable("CLUBOS_NATIVE") == "0") return false;
        try { return Imports.clubos_abi_version() == AbiVersion; }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { return false; }
    }
}
