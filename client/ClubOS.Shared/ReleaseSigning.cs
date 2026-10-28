using System.Security.Cryptography;
using System.Text;

namespace ClubOS.Shared;

/// <summary>
/// Подпись версий клиента. Обновление запускает код от имени SYSTEM на всех ПК, поэтому его подлинность проверяется не доверием к серверу или связи,
/// а подписью владельца продукта: закрытый ключ хранится у него (инструмент client/ClubOS.Release), открытый вшит в службу.
/// Подписывается не файл целиком, а короткая строка «версия, sha256 пакета, размер»: пакет сверяется с sha256, и подмена версии, пакета или размера
/// ломает подпись. ECDSA на кривой P-256, подпись 64 байта (формат IEEE P1363), в тексте base64.
/// </summary>
public static class ReleaseSigning
{
    public const string Context = "clubos-release|v1";

    public static string Payload(string version, string sha256, long size) => $"{Context}|{version}|{sha256.ToLowerInvariant()}|{size}";

    public static ECDsa GenerateKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public static string Sign(ECDsa privateKey, string version, string sha256, long size) =>
        Convert.ToBase64String(privateKey.SignData(Encoding.UTF8.GetBytes(Payload(version, sha256, size)), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    public static bool Verify(ECDsa publicKey, string version, string sha256, long size, string? signatureBase64)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(signatureBase64)) return false;
            var signature = Convert.FromBase64String(signatureBase64);
            if (signature.Length != 64) return false;
            return publicKey.VerifyData(Encoding.UTF8.GetBytes(Payload(version, sha256, size)), signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception e) when (e is FormatException or CryptographicException) { return false; }
    }

    /// <summary>Открытый ключ из PEM («-----BEGIN PUBLIC KEY-----»). Пусто, не ключ или не P-256: null (обновления тогда выключены).</summary>
    public static ECDsa? LoadPublicKey(string? pem)
    {
        if (string.IsNullOrWhiteSpace(pem) || !pem.Contains("BEGIN PUBLIC KEY", StringComparison.Ordinal)) return null;
        try
        {
            var key = ECDsa.Create();
            key.ImportFromPem(pem);
            if (key.KeySize == 256) return key;
            key.Dispose();
            return null;
        }
        catch (Exception e) when (e is ArgumentException or CryptographicException) { return null; }
    }
}
