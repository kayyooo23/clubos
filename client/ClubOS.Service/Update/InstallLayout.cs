using ClubOS.Shared;

namespace ClubOS.Service.Update;

/// <summary>
/// Где лежит программа. Версии стоят рядом, каждая в своей папке: «&lt;корень&gt;\versions\&lt;версия&gt;\{Service,Overlay}». Обновление не затирает работающие файлы:
/// новая версия ложится в соседнюю папку, а потом службе и задаче оверлея указывается новый путь. Старая версия остаётся нетронутой, поэтому
/// откат это просто возврат прежнего пути. Если служба запущена не из такой папки (разработка, старая установка), автообновление выключено.
/// </summary>
public sealed record InstallLayout(string VersionsRoot, string CurrentVersion, string CurrentDir)
{
    public string DirFor(string version) => Path.Combine(VersionsRoot, version);
    public static string ServiceExe(string versionDir) => Path.Combine(versionDir, "Service", "ClubOS.Service.exe");
    public static string OverlayExe(string versionDir) => Path.Combine(versionDir, "Overlay", "ClubOS.Overlay.exe");

    /// <summary>Из папки программы (…\versions\1.2.3\Service\). Не из такой папки: null.</summary>
    public static InstallLayout? FromBaseDirectory(string baseDirectory)
    {
        var service = new DirectoryInfo(baseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var version = service.Parent;
        var root = version?.Parent;
        if (version is null || root is null) return null;
        if (!service.Name.Equals("Service", StringComparison.OrdinalIgnoreCase) || !root.Name.Equals("versions", StringComparison.OrdinalIgnoreCase)) return null;
        return AppVersion.IsValid(version.Name) ? new InstallLayout(root.FullName, version.Name, version.FullName) : null;
    }
}
