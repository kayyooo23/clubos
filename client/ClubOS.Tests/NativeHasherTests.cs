using ClubOS.Shared.Native;
using ClubOS.Service.Sync;

namespace ClubOS.Tests;

/// <summary>
/// Нативный подсчёт хешей блоков (C++, client/ClubOS.Native) должен давать ровно то же, что подсчёт на C#. Если библиотека не собрана
/// (на ПК нет компилятора C++), эти проверки пропускаются: служба в этом случае считает на C#.
/// </summary>
public sealed class NativeHasherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clubos-native-" + Guid.NewGuid().ToString("N"));

    public NativeHasherTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string Write(string name, int size)
    {
        var data = new byte[size];
        new Random(size).NextBytes(data);
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1023)]
    [InlineData(1024)]
    [InlineData(1025)]
    [InlineData(3 * 1024 + 5)]
    [InlineData(2 * 1024 * 1024 + 17)]
    public async Task Native_hashes_equal_the_managed_hashes(int size)
    {
        if (!NativeBlockHasher.IsAvailable) return;
        var path = Write("f.bin", size);
        const int blockSize = 1024;
        var managed = await new ManifestBuilder(_dir, blockSize).HashBlocksManagedAsync(path, size, CancellationToken.None);
        var digests = NativeBlockHasher.HashBlocks(path, size, blockSize, CancellationToken.None);
        Assert.Equal(managed.Length, digests.Length / NativeBlockHasher.DigestSize);
        for (var i = 0; i < managed.Length; i++) Assert.Equal(managed[i], NativeBlockHasher.HexOf(digests, i));
    }

    [Fact]
    public void A_block_larger_than_one_read_chunk_gives_the_same_hash_as_SHA256()
    {
        if (!NativeBlockHasher.IsAvailable) return;
        var path = Write("big.bin", 3 * 1024 * 1024 + 11);
        var digests = NativeBlockHasher.HashBlocks(path, new FileInfo(path).Length, 4 * 1024 * 1024, CancellationToken.None);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(), NativeBlockHasher.HexOf(digests, 0));
    }

    [Fact]
    public void Errors_look_like_the_ones_from_the_managed_code()
    {
        if (!NativeBlockHasher.IsAvailable) return;
        Assert.Throws<IOException>(() => NativeBlockHasher.HashBlocks(Path.Combine(_dir, "нет такого.bin"), 10, 1024, CancellationToken.None));
        var path = Write("f.bin", 2000);
        var changed = Assert.Throws<InvalidDataException>(() => NativeBlockHasher.HashBlocks(path, 1999, 1024, CancellationToken.None));
        Assert.Contains("f.bin", changed.Message);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => NativeBlockHasher.HashBlocks(path, 2000, 1024, cts.Token));
    }

    [Fact]
    public async Task The_manifest_of_a_folder_built_with_the_native_part_matches_the_managed_hashes()
    {
        if (!NativeBlockHasher.IsAvailable) return;
        var game = Path.Combine(_dir, "Game");
        Directory.CreateDirectory(Path.Combine(game, "data"));
        foreach (var (name, size) in new[] { ("main.exe", 5000), ("data\\pak0.bin", 2500), ("data\\empty.bin", 0), ("readme.txt", 1024) })
        {
            var data = new byte[size];
            new Random(size + name.Length).NextBytes(data);
            File.WriteAllBytes(Path.Combine(game, name), data);
        }
        var builder = new ManifestBuilder(_dir, 1024);
        var result = await builder.BuildAsync(["Game"], null, null, CancellationToken.None);
        Assert.Null(result.Error);
        Assert.Equal(4, result.Manifest!.Files.Count);
        foreach (var file in result.Manifest.Files)
        {
            var expected = await builder.HashBlocksManagedAsync(Path.Combine(_dir, file.Path.Replace('/', '\\')), file.Size, CancellationToken.None);
            Assert.Equal(expected, file.Blocks);
        }
    }
}
