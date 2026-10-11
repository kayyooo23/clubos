using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using ClubOS.Shared;
using ClubOS.Shared.Ui;

namespace ClubOS.Overlay;

/// <summary>Запускает игры и программы из меню в сеансе гостя. Что запускать, решает только каталог сервера (см. AppLaunch).</summary>
public sealed class WindowsAppLauncher : IAppLauncher
{
    /// <summary>Для файла проверяем, что он есть на этом ПК. Для Steam проверить дёшево нельзя, поэтому считаем игру доступной: Steam сам скажет.</summary>
    public bool IsAvailable(AppInfo app) =>
        app.LaunchKind != "exe" || (AppLaunch.Plan(app, out _) is { } plan && File.Exists(plan.FileName));

    public string? Launch(AppInfo app)
    {
        var plan = AppLaunch.Plan(app, out var error);
        if (plan is null) return error;
        try
        {
            Process.Start(new ProcessStartInfo(plan.FileName)
            {
                UseShellExecute = plan.UseShellExecute,
                WorkingDirectory = plan.WorkingDirectory ?? "",
            })?.Dispose();
            Log.Info($"Запущено из меню: {app.Title} ({app.LaunchKind})");
            return null;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Log.Error($"Не удалось запустить «{app.Title}»", e);
            return app.LaunchKind == "steam"
                ? $"Не удалось запустить «{app.Title}». Возможно, Steam не установлен на этом ПК"
                : $"Не удалось запустить «{app.Title}». Сообщите администратору";
        }
    }
}
