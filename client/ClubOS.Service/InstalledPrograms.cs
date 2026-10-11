using System.Text.RegularExpressions;

namespace ClubOS.Service;

/// <summary>Программа, найденная на ПК: название ярлыка, полный путь к exe и иконка (PNG), если её удалось достать.</summary>
public sealed record ProgramEntry(string Name, string Path, byte[]? Icon = null);

/// <summary>Раскрывает ярлык .lnk в путь к файлу, который он запускает. В тестах подменяется.</summary>
public interface IShortcutResolver
{
    string? Target(string shortcutPath);
}

/// <summary>Читает ярлыки через COM-объект WScript.Shell (есть в любой Windows). Если не получилось, ярлык просто пропускается.</summary>
public sealed class WshShortcutResolver : IShortcutResolver
{
    private readonly object? _shell;

    public WshShortcutResolver()
    {
        try { _shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!); }
        catch { _shell = null; }
    }

    public string? Target(string shortcutPath)
    {
        if (_shell is null) return null;
        try
        {
            dynamic shell = _shell;
            dynamic link = shell.CreateShortcut(shortcutPath);
            string? target = link.TargetPath;
            System.Runtime.InteropServices.Marshal.ReleaseComObject(link);
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch { return null; }
    }
}

/// <summary>
/// Какие программы стоят на ПК: берём ярлыки из меню «Пуск» (общего и каждого пользователя Windows, в том числе установленные «только для меня»)
/// и с рабочих столов, раскрываем их в путь к exe и оставляем настоящие программы. Администратор выбирает из этого списка программу для меню гостя,
/// поэтому путь заведомо существует на ПК клуба.
/// </summary>
public static class InstalledPrograms
{
    public const int DefaultLimit = 600;

    // та же форма пути, что принимает сервер (см. check_launch_target): диск, обратные слэши, без «..», оканчивается на .exe
    private static readonly Regex ExePath = new(@"^[A-Za-z]:\\[^<>:""/|?*\x00-\x1f]+\.exe$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // служебные файлы рядом с программой: их не показываем
    private static readonly string[] SkipFileStarts = ["unins", "uninstall", "setup", "install", "update", "updater", "crash", "error_report", "vc_redist", "dxsetup", "msiexec"];
    private static readonly string[] SkipNameParts = ["uninstall", "удалить", "удаление", "readme", "справк", "help", "license", "лиценз", "website", "сайт", "release notes", "manual", "руководство", "changelog"];

    /// <summary>Папки, где лежат ярлыки: общее меню «Пуск», меню «Пуск» и рабочий стол каждого пользователя, общий рабочий стол.</summary>
    public static IReadOnlyList<string> DefaultRoots(string? usersRoot = null, string? commonStartMenu = null, string? publicDesktop = null)
    {
        var roots = new List<string>();
        void Add(string? path) { if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) roots.Add(path); }

        var common = commonStartMenu ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
        Add(System.IO.Path.Combine(common, "Programs"));
        usersRoot ??= System.IO.Path.Combine(System.IO.Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "Users");
        Add(publicDesktop ?? System.IO.Path.Combine(usersRoot, "Public", "Desktop"));
        try
        {
            foreach (var profile in Directory.GetDirectories(usersRoot))
            {
                var name = System.IO.Path.GetFileName(profile);
                if (name is "Default" or "Default User" or "All Users" or "Public") continue;
                Add(System.IO.Path.Combine(profile, "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs"));
                Add(System.IO.Path.Combine(profile, "Desktop"));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* чужой профиль недоступен: идём дальше */ }
        return roots;
    }

    /// <summary>Подходит ли найденный ярлык: настоящая программа, а не удаление, справка или системный файл.</summary>
    public static bool Accept(string shortcutName, string target, Func<string, bool> exists, string? windowsDir = null)
    {
        if (!ExePath.IsMatch(target) || target.Length > 260) return false;
        if (target.Split('\\').Skip(1).Any(p => p is "" or "." or "..")) return false;
        windowsDir ??= Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (windowsDir.Length > 0 && target.StartsWith(windowsDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return false;
        var file = System.IO.Path.GetFileNameWithoutExtension(target);
        if (SkipFileStarts.Any(s => file.StartsWith(s, StringComparison.OrdinalIgnoreCase))) return false;
        if (SkipNameParts.Any(s => shortcutName.Contains(s, StringComparison.OrdinalIgnoreCase))) return false;
        return exists(target);
    }

    public static IReadOnlyList<ProgramEntry> Scan(IEnumerable<string> roots, IShortcutResolver resolver, Func<string, bool>? exists = null, int limit = DefaultLimit, string? windowsDir = null,
        IProgramIcons? icons = null)
    {
        exists ??= File.Exists;
        var found = new Dictionary<string, ProgramEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 4 }).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var lnk in files)
            {
                var target = resolver.Target(lnk);
                var name = System.IO.Path.GetFileNameWithoutExtension(lnk).Trim();
                if (target is null || name.Length == 0 || !Accept(name, target, exists, windowsDir)) continue;
                if (name.Length > 120) name = name[..120];
                // один и тот же файл из нескольких ярлыков (меню «Пуск» и рабочий стол): оставляем короткое название
                if (!found.TryGetValue(target, out var old) || name.Length < old.Name.Length) found[target] = new ProgramEntry(name, target);
            }
        }
        var list = found.Values.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).Take(limit).ToList();
        if (icons is null || list.Count == 0) return list;
        var pictures = icons.For(list.Select(p => p.Path).ToList());
        return list.Select(p => pictures.TryGetValue(p.Path, out var png) ? p with { Icon = png } : p).ToList();
    }

    public static IReadOnlyList<ProgramEntry> ScanDefault() => Scan(DefaultRoots(), new WshShortcutResolver(), icons: new ShellProgramIcons());
}
