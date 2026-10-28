using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ClubOS.Shared.Native;

/// <summary>
/// Подсчёт SHA-256 блоков файла в нативной библиотеке (C++). Нужен синхронизации игр: списки файлов с хешами блоков считаются по десяткам гигабайт.
/// Если библиотеки нет, <see cref="ClubosNative.IsAvailable"/> равно false, и вызывающий считает на C#: результат одинаковый.
/// </summary>
public static class NativeBlockHasher
{
    public const int DigestSize = 32;

    private const int Ok = 0, ErrOpen = 1, ErrRead = 2, ErrChanged = 3, ErrArgs = 4, ErrCancelled = 5, ErrCrypto = 6;

    public static bool IsAvailable => ClubosNative.IsAvailable;

    /// <summary>
    /// Хеши блоков файла (по 32 байта на блок подряд). Ошибки те же, что у чтения через FileStream: IOException (в том числе при смене размера файла
    /// во время подсчёта) и OperationCanceledException при отмене. Блокирует поток до конца работы: вызывать из Task.Run.
    /// </summary>
    public static byte[] HashBlocks(string path, long length, int blockSize, CancellationToken ct)
    {
        var blocks = (int)((length + blockSize - 1) / blockSize);
        var digests = new byte[blocks * DigestSize];
        var flag = Marshal.AllocHGlobal(sizeof(int));
        Marshal.WriteInt32(flag, 0);
        try
        {
            using var registration = ct.Register(static state => Marshal.WriteInt32((IntPtr)state!, 1), flag);
            var code = Imports.clubos_hash_blocks(path, length, blockSize, digests, digests.Length, flag, out var osError);
            switch (code)
            {
                case Ok: return digests;
                case ErrCancelled: throw new OperationCanceledException(ct);
                case ErrChanged: throw new InvalidDataException($"Файл изменился во время подсчёта: {Path.GetFileName(path)}");
                case ErrOpen or ErrRead: throw new IOException(new Win32Exception((int)osError).Message + $": {Path.GetFileName(path)}");
                case ErrCrypto: throw new IOException("Системная криптография недоступна");
                case ErrArgs: throw new ArgumentException($"Неверные аргументы нативного подсчёта: {Path.GetFileName(path)}");
                default: throw new IOException($"Сбой нативного подсчёта хешей (код {code})");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(flag);
        }
    }

    /// <summary>Хеш i-го блока в шестнадцатеричном виде (строчные буквы, как во всём списке файлов).</summary>
    public static string HexOf(byte[] digests, int index) => Convert.ToHexString(digests.AsSpan(index * DigestSize, DigestSize)).ToLowerInvariant();
}
