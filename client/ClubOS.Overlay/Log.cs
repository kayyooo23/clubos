using System.IO;
using System.Text;

namespace ClubOS.Overlay;

/// <summary>Минимальный журнал в %LOCALAPPDATA%\ClubOS\overlay.log. Паролей, кодов и токенов сюда не пишем — только события и ошибки.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClubOS", "overlay.log");

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string message, Exception? e = null) =>
        Write("ERROR", e is null ? message : $"{message}: {e.GetType().Name}: {e.Message}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                if (File.Exists(Path) && new FileInfo(Path).Length > 512 * 1024) File.Move(Path, Path + ".old", true);
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch { /* журнал не должен ронять экран блокировки */ }
    }
}
