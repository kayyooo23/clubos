using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClubOS.Shared;

namespace ClubOS.Service;

/// <summary>Что служба получила при подключении по коду: ключ ПК и то, как этот ПК называется в клубе.</summary>
public sealed record PcIdentity(string Key, int? PcId = null, string? PcName = null, string? ClubName = null);

/// <summary>Где служба помнит полученный ключ ПК. Если запомнить не вышло, ничего страшного: служба подключится по коду заново, сервер узнает тот же компьютер.</summary>
public interface IIdentityStore
{
    PcIdentity? Load();
    void Save(PcIdentity? identity);   // null = забыть
}

public sealed class MemoryIdentityStore : IIdentityStore
{
    public PcIdentity? Value { get; private set; }
    public PcIdentity? Load() => Value;
    public void Save(PcIdentity? identity) => Value = identity;
}

/// <summary>Файл с ключом ПК, зашифрованный Windows DPAPI (область «компьютер»). Папка закрыта правами скриптом установки; гость файл не прочитает.</summary>
public sealed class DpapiIdentityStore(string path, Action<string>? warn = null) : IIdentityStore
{
    private static readonly byte[] Entropy = "ClubOS.identity.v1"u8.ToArray();

    public PcIdentity? Load()
    {
        try
        {
            if (!File.Exists(path)) return null;
            var raw = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.LocalMachine);
            var identity = JsonSerializer.Deserialize<PcIdentity>(raw, Json.Options);
            return string.IsNullOrWhiteSpace(identity?.Key) ? null : identity;
        }
        catch (Exception e)
        {
            warn?.Invoke($"Не удалось прочитать сохранённый ключ ПК: {e.GetType().Name}");
            return null;
        }
    }

    public void Save(PcIdentity? identity)
    {
        try
        {
            if (identity is null)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(identity, Json.Options), Entropy, DataProtectionScope.LocalMachine));
        }
        catch (Exception e)
        {
            warn?.Invoke($"Не удалось сохранить ключ ПК на диск ({e.GetType().Name}); после перезапуска службы ПК подключится по коду заново");
        }
    }
}
