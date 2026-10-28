using System.Reflection;
using System.Security.Cryptography;
using ClubOS.Shared;

namespace ClubOS.Service.Update;

/// <summary>
/// Открытый ключ, которым проверяется подпись версий. Вшит в службу (файл release-public-key.pem в проекте): подменить его можно только вместе
/// с самой программой, то есть он надёжнее любой настройки. Пока в файле нет ключа, автообновление выключено, и это безопасное состояние по умолчанию.
/// Для разработки ключ можно подставить переменной CLUBOS_RELEASE_PUBLIC_KEY_FILE (служба работает от системы, её окружение меняет только администратор).
/// </summary>
public static class ReleaseKey
{
    public const string ResourceName = "release-public-key.pem";

    public static ECDsa? Load()
    {
        var overridePath = Environment.GetEnvironmentVariable("CLUBOS_RELEASE_PUBLIC_KEY_FILE");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            try { return ReleaseSigning.LoadPublicKey(File.ReadAllText(overridePath)); }
            catch (IOException) { return null; }
        }
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return ReleaseSigning.LoadPublicKey(reader.ReadToEnd());
    }
}
