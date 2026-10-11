using System.Security.Cryptography;
using System.Text;

namespace ClubOS.Service;

/// <summary>Где служба помнит токен вошедшего на этом ПК гостя/сотрудника, чтобы перезапуск службы не «терял» вход.</summary>
public interface ITokenStore
{
    string? Load();
    void Save(string? token);   // null = забыть
}

/// <summary>Только в памяти (тесты, режим без диска).</summary>
public sealed class MemoryTokenStore : ITokenStore
{
    public string? Value { get; private set; }
    public string? Load() => Value;
    public void Save(string? token) => Value = token;
}

/// <summary>
/// Файл с токеном, зашифрованный Windows DPAPI (область «компьютер»). Читает и пишет его только служба;
/// права на папку (система и администраторы) выставляет скрипт установки. Токен действует только вместе с ключом
/// этого ПК, живёт до выхода/бездействия и сам по себе ничего не открывает.
/// Если записать не удалось — работаем дальше с токеном в памяти (после перезапуска службы гостю придётся войти заново).
/// </summary>
public sealed class DpapiTokenStore(string path, Action<string>? warn = null) : ITokenStore
{
    private static readonly byte[] Entropy = "ClubOS.token.v1"u8.ToArray();

    public static string DefaultPath =>
        Environment.GetEnvironmentVariable("CLUBOS_TOKEN_FILE")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ClubOS", "token.bin");

    public string? Load()
    {
        try
        {
            if (!File.Exists(path)) return null;
            var raw = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(raw);
        }
        catch (Exception e)   // сохранение «по возможности»: любая ошибка диска не должна ломать вход гостя
        {
            warn?.Invoke($"Не удалось прочитать сохранённый вход: {e.GetType().Name}");
            return null;
        }
    }

    public void Save(string? token)
    {
        try
        {
            if (token is null)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.LocalMachine));
        }
        catch (Exception e)
        {
            warn?.Invoke($"Не удалось сохранить вход на диск ({e.GetType().Name}); он останется только в памяти");
        }
    }
}
