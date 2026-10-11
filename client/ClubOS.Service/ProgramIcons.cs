using System.IO.Compression;
using System.Runtime.InteropServices;

namespace ClubOS.Service;

/// <summary>Достаёт иконки программ. В тестах подменяется.</summary>
public interface IProgramIcons
{
    /// <summary>PNG-иконка для каждого пути, где её удалось получить (остальные в словаре отсутствуют).</summary>
    IReadOnlyDictionary<string, byte[]> For(IReadOnlyList<string> exePaths);
}

/// <summary>Собирает PNG из сырых пикселей без внешних библиотек.</summary>
internal static class PngEncoder
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in type) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> be = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(be, (uint)data.Length);
        s.Write(be);
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(be, Crc(t, data));
        s.Write(be);
    }

    /// <summary>bgra: пиксели по четыре байта (синий, зелёный, красный, прозрачность) сверху вниз. premultiplied: цвет уже умножен на прозрачность (так отдаёт Windows).</summary>
    public static byte[] Encode(int width, int height, byte[] bgra, bool premultiplied)
    {
        var stride = width * 4;
        var raw = new byte[(stride + 1) * height];
        for (var y = 0; y < height; y++)
        {
            var row = y * (stride + 1);
            raw[row] = 0;                                       // без фильтра
            for (var x = 0; x < width; x++)
            {
                var i = y * stride + x * 4;
                int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2], a = bgra[i + 3];
                if (premultiplied && a is > 0 and < 255) { b = Math.Min(255, b * 255 / a); g = Math.Min(255, g * 255 / a); r = Math.Min(255, r * 255 / a); }
                var o = row + 1 + x * 4;
                raw[o] = (byte)r; raw[o + 1] = (byte)g; raw[o + 2] = (byte)b; raw[o + 3] = (byte)a;
            }
        }
        using var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), (uint)width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), (uint)height);
        header[8] = 8; header[9] = 6;                           // 8 бит на канал, RGBA
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", packed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }
}

/// <summary>
/// Иконка программы так, как её показывает Windows (из ресурсов exe): IShellItemImageFactory отдаёт готовую картинку нужного размера с прозрачностью.
/// Работает в отдельном потоке STA, как требует оболочка. Если у программы нет своей иконки или получить её не вышло, программа остаётся без иконки.
/// </summary>
public sealed class ShellProgramIcons : IProgramIcons
{
    public const int Size = 96;
    public const int SmallSize = 64;
    public const int MaxBytes = 28_000;       // сервер принимает иконку до 30 КБ

    [StructLayout(LayoutKind.Sequential)] private struct Size2 { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int biSize, biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(Size2 size, uint flags, out IntPtr hbitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);
    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr handle, int count, out BitmapInfo bitmap);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[]? bits, ref BitmapInfoHeader info, uint usage);

    private const uint IconOnly = 0x4;

    public IReadOnlyDictionary<string, byte[]> For(IReadOnlyList<string> exePaths)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var thread = new Thread(() =>
        {
            foreach (var path in exePaths)
            {
                try
                {
                    var png = Extract(path, Size);
                    if (png is { Length: > MaxBytes }) png = Extract(path, SmallSize);
                    if (png is { Length: > 0 and <= MaxBytes }) result[path] = png;
                }
                catch { /* без иконки */ }
            }
        }) { IsBackground = true, Name = "Иконки программ" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(60));          // зависший файл не должен держать службу
        lock (result) return new Dictionary<string, byte[]>(result, StringComparer.OrdinalIgnoreCase);
    }

    private static byte[]? Extract(string path, int size)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory) != 0 || factory is null) return null;
        IntPtr hbitmap = IntPtr.Zero;
        try
        {
            if (factory.GetImage(new Size2 { cx = size, cy = size }, IconOnly, out hbitmap) != 0 || hbitmap == IntPtr.Zero) return null;
            if (GetObject(hbitmap, Marshal.SizeOf<BitmapInfo>(), out var bmp) == 0 || bmp.bmWidth <= 0 || bmp.bmHeight <= 0) return null;
            var info = new BitmapInfoHeader { biSize = Marshal.SizeOf<BitmapInfoHeader>(), biWidth = bmp.bmWidth, biHeight = -bmp.bmHeight, biPlanes = 1, biBitCount = 32 };
            var pixels = new byte[bmp.bmWidth * bmp.bmHeight * 4];
            var dc = CreateCompatibleDC(IntPtr.Zero);
            try { if (GetDIBits(dc, hbitmap, 0, (uint)bmp.bmHeight, pixels, ref info, 0) == 0) return null; }
            finally { DeleteDC(dc); }
            // у иконок без прозрачности Windows иногда отдаёт нулевой альфа-канал: тогда считаем картинку непрозрачной
            var anyAlpha = false;
            for (var i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { anyAlpha = true; break; }
            if (!anyAlpha) for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            return PngEncoder.Encode(bmp.bmWidth, bmp.bmHeight, pixels, premultiplied: true);
        }
        finally
        {
            if (hbitmap != IntPtr.Zero) DeleteObject(hbitmap);
            Marshal.ReleaseComObject(factory);
        }
    }
}
