namespace ClubOS.Shared.Ui;

/// <summary>
/// Браузеры из меню гостя всегда открываются в приватном режиме (инкогнито): история, пароли и куки гостя не остаются на ПК для следующего.
/// Ключ запуска подбирается по имени запускаемого файла из этой таблицы; гость и сервер аргументов не задают, поэтому подменить их нельзя.
/// Остальные программы запускаются как есть: приватного режима у них нет.
/// Это именно режим запуска: если гость сам нажмёт в браузере Ctrl+N, откроется обычное окно. Чтобы браузер принудительно работал только
/// в приватном режиме, нужны политики браузера на ПК (см. client/README.md).
/// </summary>
public static class PrivateBrowsing
{
    /// <summary>Exe: имя файла. Flag: ключ приватного режима. PathHint: часть пути, если имя файла слишком общее (browser.exe у Яндекса, launcher.exe у Opera).</summary>
    private sealed record Browser(string Exe, string Flag, string? PathHint = null);

    private static readonly Browser[] Known =
    [
        new("chrome.exe", "--incognito"),
        new("msedge.exe", "--inprivate"),
        new("firefox.exe", "-private-window"),
        new("brave.exe", "--incognito"),
        new("vivaldi.exe", "--incognito"),
        new("chromium.exe", "--incognito"),
        new("opera.exe", "--private"),
        new("launcher.exe", "--private", @"\Opera"),                // Opera запускается через launcher.exe в своей папке
        new("browser.exe", "--incognito", @"\YandexBrowser\"),      // Яндекс Браузер: browser.exe, имя файла само по себе ничего не значит
    ];

    /// <summary>Ключ запуска для этого exe: приватный режим, если это известный браузер; иначе null (программа запускается без аргументов).</summary>
    public static string? ArgumentsFor(string exePath)
    {
        var name = exePath[(exePath.LastIndexOf('\\') + 1)..];
        foreach (var b in Known)
        {
            if (!name.Equals(b.Exe, StringComparison.OrdinalIgnoreCase)) continue;
            if (b.PathHint is null || exePath.Contains(b.PathHint, StringComparison.OrdinalIgnoreCase)) return b.Flag;
        }
        return null;
    }
}
