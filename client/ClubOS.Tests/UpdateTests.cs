using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ClubOS.Service;
using ClubOS.Service.Update;
using ClubOS.Shared;
using Microsoft.Extensions.Time.Testing;

namespace ClubOS.Tests;

public class VersionAndSigningTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2.3.0", "1.2.3")]
    [InlineData("1.2.3.4", "1.2.3.4")]
    [InlineData("1.2.3+abcdef0", "1.2.3")]
    [InlineData("1.2.3-beta", "1.2.3")]
    [InlineData("  2.0.0 ", "2.0.0")]
    [InlineData("1.2", null)]
    [InlineData("abc", null)]
    [InlineData("1.2.3; calc", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Versions_are_normalized_or_rejected(string? raw, string? expected) => Assert.Equal(expected, AppVersion.Normalize(raw));

    [Fact]
    public void Versions_compare_as_numbers_not_as_text()
    {
        Assert.True(AppVersion.IsNewer("1.10.0", "1.9.0"));
        Assert.True(AppVersion.IsNewer("2.0.0", "1.99.99.99"));
        Assert.False(AppVersion.IsNewer("1.2.0", "1.2.0.0"));
        Assert.False(AppVersion.IsNewer("1.2.0", "1.2.1"));
        Assert.Equal(0, AppVersion.Compare("1.2.3", "1.2.3.0"));
        Assert.True(AppVersion.IsValid(AppVersion.Current));            // версия самой программы собрана и читается
    }

    private static readonly string Sha = new('a', 64);

    [Fact]
    public void A_signature_verifies_only_for_exactly_what_was_signed()
    {
        using var key = ReleaseSigning.GenerateKey();
        var signature = ReleaseSigning.Sign(key, "1.2.0", Sha, 1234);
        Assert.True(ReleaseSigning.Verify(key, "1.2.0", Sha, 1234, signature));
        Assert.False(ReleaseSigning.Verify(key, "1.2.1", Sha, 1234, signature));                       // другая версия
        Assert.False(ReleaseSigning.Verify(key, "1.2.0", new string('b', 64), 1234, signature));        // другой пакет
        Assert.False(ReleaseSigning.Verify(key, "1.2.0", Sha, 1235, signature));                       // другой размер
        using var other = ReleaseSigning.GenerateKey();
        Assert.False(ReleaseSigning.Verify(other, "1.2.0", Sha, 1234, signature));                     // чужой ключ
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64!!")]
    [InlineData("QUJD")]                                                                               // base64, но не подпись (3 байта)
    public void A_malformed_signature_is_simply_false(string? signature)
    {
        using var key = ReleaseSigning.GenerateKey();
        Assert.False(ReleaseSigning.Verify(key, "1.2.0", Sha, 1234, signature));
    }

    [Fact]
    public void The_public_key_loads_from_pem_and_garbage_means_updates_off()
    {
        using var key = ReleaseSigning.GenerateKey();
        using var loaded = ReleaseSigning.LoadPublicKey(key.ExportSubjectPublicKeyInfoPem());
        Assert.NotNull(loaded);
        Assert.True(ReleaseSigning.Verify(loaded!, "1.0.1", Sha, 5, ReleaseSigning.Sign(key, "1.0.1", Sha, 5)));
        Assert.Null(ReleaseSigning.LoadPublicKey(null));
        Assert.Null(ReleaseSigning.LoadPublicKey("Здесь будет ключ"));
        Assert.Null(ReleaseSigning.LoadPublicKey("-----BEGIN PUBLIC KEY-----\nAAAA\n-----END PUBLIC KEY-----"));
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.Null(ReleaseSigning.LoadPublicKey(p384.ExportSubjectPublicKeyInfoPem()));               // другая кривая не принимается
    }
}

public class SafeUnzipTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "clubos-test-unzip-" + Guid.NewGuid().ToString("N"));

    public SafeUnzipTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    public static byte[] Zip(params (string Name, string Body)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (name, body) in entries)
            {
                var e = zip.CreateEntry(name);
                using var s = e.Open();
                s.Write(Encoding.UTF8.GetBytes(body));
            }
        return ms.ToArray();
    }

    private string Write(byte[] zip)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        File.WriteAllBytes(path, zip);
        return path;
    }

    private string Out => Path.Combine(_root, "out");

    [Fact]
    public void A_normal_package_is_unpacked_into_its_folders()
    {
        var error = SafeUnzip.Extract(Write(Zip(("Service/ClubOS.Service.exe", "svc"), ("Overlay/ClubOS.Overlay.exe", "ovl"), ("Overlay/Fonts/a.ttf", "f"), ("version.txt", "1.2.0"))), Out);
        Assert.Null(error);
        Assert.Equal("svc", File.ReadAllText(Path.Combine(Out, "Service", "ClubOS.Service.exe")));
        Assert.Equal("f", File.ReadAllText(Path.Combine(Out, "Overlay", "Fonts", "a.ttf")));
        Assert.Equal("1.2.0", File.ReadAllText(Path.Combine(Out, "version.txt")));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("Service/../../evil.txt")]
    [InlineData("/etc/evil.txt")]
    [InlineData("C:/Windows/evil.txt")]
    [InlineData("Service/file.txt:stream")]
    [InlineData("Service\\..\\..\\evil.txt")]
    [InlineData("Other/file.txt")]
    [InlineData("install.ps1")]
    public void Dangerous_or_unexpected_paths_are_refused_and_nothing_is_written(string name)
    {
        var error = SafeUnzip.Extract(Write(Zip(("Service/ClubOS.Service.exe", "ok"), (name, "x"))), Out);
        Assert.NotNull(error);
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
        Assert.False(Directory.Exists(Out));                                          // проверка идёт до распаковки: не создаётся даже папка
    }

    [Fact]
    public void Too_many_or_too_big_packages_are_refused()
    {
        var many = Zip(Enumerable.Range(0, 6).Select(i => ($"Service/f{i}.txt", "x")).ToArray());
        Assert.Contains("слишком много", SafeUnzip.Extract(Write(many), Out, new SafeUnzip.Limits(MaxEntries: 5)));
        var big = Zip(("Service/a.bin", new string('x', 2000)));
        Assert.Contains("слишком большой", SafeUnzip.Extract(Write(big), Out, new SafeUnzip.Limits(MaxEntryBytes: 1000)));
        Assert.Contains("слишком большой", SafeUnzip.Extract(Write(Zip(("Service/a", new string('x', 800)), ("Service/b", new string('x', 800)))), Out + "2", new SafeUnzip.Limits(MaxTotalBytes: 1000)));
    }

    [Fact]
    public void A_broken_or_empty_archive_is_an_error_not_a_crash()
    {
        Assert.NotNull(SafeUnzip.Extract(Write(Encoding.UTF8.GetBytes("это не zip")), Out));
        Assert.Equal("Пакет пустой", SafeUnzip.Extract(Write(Zip()), Out));
        Assert.NotNull(SafeUnzip.Extract(Path.Combine(_root, "нет-такого.zip"), Out));
    }

    [Fact]
    public void The_validator_checks_files_and_versions()
    {
        SafeUnzip.Extract(Write(Zip(("Service/ClubOS.Service.exe", "s"), ("Service/ClubOS.Service.dll", "s"), ("Overlay/ClubOS.Overlay.exe", "o"), ("Overlay/ClubOS.Overlay.dll", "o"), ("version.txt", "1.2.0\n"))), Out);
        Assert.Null(new PackageValidator(_ => "1.2.0").Validate(Out, "1.2.0"));
        Assert.Null(new PackageValidator(_ => "1.2.0.0+abc").Validate(Out, "1.2.0"));                   // «1.2.0.0» и хвост сборки это та же версия
        Assert.Contains("не совпадает", new PackageValidator(_ => "1.2.0").Validate(Out, "1.3.0"));        // version.txt говорит другое
        Assert.Contains("не совпадает", new PackageValidator(_ => "9.9.9").Validate(Out, "1.2.0"));        // версия самой программы другая
        Assert.Contains("«нет»", new PackageValidator(_ => null).Validate(Out, "1.2.0"));                  // версию прочитать не удалось: честно сказано
        File.Delete(Path.Combine(Out, "Overlay", "ClubOS.Overlay.exe"));
        Assert.Contains("нет службы или оверлея", new PackageValidator(_ => "1.2.0").Validate(Out, "1.2.0"));
        File.WriteAllText(Path.Combine(Out, "Overlay", "ClubOS.Overlay.exe"), "o");
        File.Delete(Path.Combine(Out, "Overlay", "ClubOS.Overlay.dll"));
        Assert.Contains("нет службы или оверлея", new PackageValidator(_ => "1.2.0").Validate(Out, "1.2.0"));
    }

    [Fact]
    public void The_layout_is_recognized_only_for_versioned_folders()
    {
        var layout = InstallLayout.FromBaseDirectory(@"C:\Program Files\ClubOS\versions\1.4.0\Service\");
        Assert.NotNull(layout);
        Assert.Equal("1.4.0", layout!.CurrentVersion);
        Assert.Equal(@"C:\Program Files\ClubOS\versions", layout.VersionsRoot);
        Assert.Equal(@"C:\Program Files\ClubOS\versions\1.5.0", layout.DirFor("1.5.0"));
        Assert.Null(InstallLayout.FromBaseDirectory(@"C:\Program Files\ClubOS\Service\"));              // старая установка
        Assert.Null(InstallLayout.FromBaseDirectory(@"C:\dev\clubos\client\ClubOS.Service\bin\Debug\net10.0-windows\"));   // разработка
        Assert.Null(InstallLayout.FromBaseDirectory(@"C:\Program Files\ClubOS\versions\latest\Service\"));  // папка не по версии
    }
}

public class UpdateInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "clubos-test-upd-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ReleaseSigning.GenerateKey();
    private readonly FakeServer _server = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));

    public UpdateInstallerTests() { Directory.CreateDirectory(_root); Directory.CreateDirectory(Path.Combine(_root, "versions", "1.0.0")); }
    public void Dispose() { _key.Dispose(); try { Directory.Delete(_root, true); } catch (IOException) { } }

    private InstallLayout Layout => new(Path.Combine(_root, "versions"), "1.0.0", Path.Combine(_root, "versions", "1.0.0"));
    private string Downloads => Path.Combine(_root, "updates", "downloads");
    private BadVersions Bad => new(Path.Combine(_root, "updates", "bad.json"), _time);

    private UpdateInstaller Installer(ECDsa? key = null, Func<string, long>? free = null, Func<string, string?>? exeVersion = null)
    {
        var api = new ServerApi(new HttpClient(_server) { BaseAddress = new Uri("http://club.test") }, FakeServer.PcKey);
        return new UpdateInstaller(api, key ?? _key, Layout, Downloads, Bad, new PackageValidator(exeVersion ?? (_ => "1.1.0")), free ?? (_ => long.MaxValue / 8));
    }

    private static byte[] GoodZip(string version = "1.1.0") => SafeUnzipTests.Zip(("Service/ClubOS.Service.exe", "svc " + version), ("Service/ClubOS.Service.dll", "svc " + version),
        ("Overlay/ClubOS.Overlay.exe", "ovl " + version), ("Overlay/ClubOS.Overlay.dll", "ovl " + version), ("version.txt", version));

    private UpdateOffer Offer(byte[] zip, string version = "1.1.0", ECDsa? signer = null, int id = 5, string? sha = null, long? size = null)
    {
        var hash = sha ?? Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
        var length = size ?? zip.Length;
        return new UpdateOffer(id, version, hash, length, ReleaseSigning.Sign(signer ?? _key, version, hash, length), "заметки", "http://evil.example/steal.zip");
    }

    private void Serve(byte[] zip, int id = 5) => _server.OnBytes("GET", $"/api/agent/update/package/{id}", 200, zip);

    [Fact]
    public async Task A_signed_package_is_downloaded_verified_unpacked_and_left_ready_next_to_the_current_version()
    {
        var zip = GoodZip();
        Serve(zip);
        var r = await Installer().PrepareAsync(Offer(zip), "1.0.0", default);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(Path.Combine(_root, "versions", "1.1.0"), r.Dir);
        Assert.True(File.Exists(Path.Combine(r.Dir!, "Service", "ClubOS.Service.exe")));
        Assert.False(Directory.Exists(r.Dir + ".tmp"));
        Assert.Empty(Directory.GetFiles(Downloads));                                                    // скачанный zip убран
        Assert.True(Directory.Exists(Path.Combine(_root, "versions", "1.0.0")));                        // работающая версия не тронута
        var request = Assert.Single(_server.Requests);
        Assert.Equal("/api/agent/update/package/5", request.Path);                                      // адрес из ответа сервера не используется
        Assert.Equal(FakeServer.PcKey, request.PcKey);
    }

    [Fact]
    public async Task A_package_signed_by_someone_else_is_never_even_downloaded_and_is_remembered_as_bad()
    {
        var zip = GoodZip();
        using var attacker = ReleaseSigning.GenerateKey();
        Serve(zip);
        var r = await Installer().PrepareAsync(Offer(zip, signer: attacker), "1.0.0", default);
        Assert.False(r.Ok);
        Assert.True(r.Permanent);
        Assert.Contains("Подпись", r.Error);
        Assert.Empty(_server.Requests);
        Assert.True(Bad.IsBad("1.1.0"));
        Assert.False(Directory.Exists(Path.Combine(_root, "versions", "1.1.0")));
    }

    [Fact]
    public async Task Tampered_offers_are_refused_whichever_field_was_changed()
    {
        var zip = GoodZip();
        Serve(zip);
        var good = Offer(zip);
        foreach (var bad in new[] { good with { Version = "1.2.0" }, good with { Sha256 = new string('c', 64) }, good with { Size = good.Size + 1 } })
        {
            var r = await Installer().PrepareAsync(bad, "1.0.0", default);
            Assert.False(r.Ok);
        }
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task A_downloaded_file_that_differs_from_the_signed_one_is_refused()
    {
        var zip = GoodZip();
        var swapped = GoodZip("1.1.0").Select(b => (byte)(b ^ 0)).ToArray();
        swapped[^1] ^= 0xFF;                                                                              // тот же размер, другое содержимое
        Serve(swapped);
        var r = await Installer().PrepareAsync(Offer(zip), "1.0.0", default);
        Assert.False(r.Ok);
        Assert.True(r.Permanent);
        Assert.Contains("не совпал", r.Error);
        Assert.Empty(Directory.GetFiles(Downloads));
        Assert.False(Directory.Exists(Path.Combine(_root, "versions", "1.1.0")));
    }

    [Fact]
    public async Task Only_a_newer_version_is_accepted_and_a_recently_failed_one_is_skipped()
    {
        var zip = GoodZip();
        Serve(zip);
        Assert.False((await Installer().PrepareAsync(Offer(zip), "1.1.0", default)).Ok);                // уже стоит
        Assert.False((await Installer().PrepareAsync(Offer(zip), "2.0.0", default)).Ok);                // новее установленной нужна
        Assert.False((await Installer().PrepareAsync(Offer(zip) with { Version = "bad version" }, "1.0.0", default)).Ok);
        Bad.Add("1.1.0");
        var skipped = await Installer().PrepareAsync(Offer(zip), "1.0.0", default);
        Assert.False(skipped.Ok);
        Assert.False(skipped.Permanent);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task Without_a_signing_key_in_the_program_nothing_is_installed()
    {
        var zip = GoodZip();
        var api = new ServerApi(new HttpClient(_server) { BaseAddress = new Uri("http://club.test") }, FakeServer.PcKey);
        var installer = new UpdateInstaller(api, null, Layout, Downloads, Bad);
        var r = await installer.PrepareAsync(Offer(zip), "1.0.0", default);
        Assert.False(r.Ok);
        Assert.Contains("ключ", r.Error);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task A_package_with_a_path_escape_is_refused_even_though_it_is_signed()
    {
        var zip = SafeUnzipTests.Zip(("Service/ClubOS.Service.exe", "s"), ("Service/../../evil.txt", "x"));
        Serve(zip);
        var r = await Installer().PrepareAsync(Offer(zip), "1.0.0", default);
        Assert.False(r.Ok);
        Assert.True(r.Permanent);
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
        Assert.False(Directory.Exists(Path.Combine(_root, "versions", "1.1.0.tmp")));
    }

    [Fact]
    public async Task A_package_that_claims_a_different_version_inside_is_refused()
    {
        var zip = GoodZip("1.5.0");                                                                         // подписан как 1.1.0, а внутри 1.5.0
        Serve(zip);
        var r = await Installer().PrepareAsync(Offer(zip, "1.1.0"), "1.0.0", default);
        Assert.False(r.Ok);
        Assert.True(r.Permanent);
        Assert.False(Directory.Exists(Path.Combine(_root, "versions", "1.1.0")));
        var viaExe = await Installer(exeVersion: _ => "0.0.1").PrepareAsync(Offer(GoodZip(), "1.1.0"), "1.0.0", default);   // версия внутри exe не та
        Assert.False(viaExe.Ok);
    }

    [Fact]
    public async Task An_already_prepared_version_is_reused_without_downloading_again()
    {
        var zip = GoodZip();
        Serve(zip);
        var first = await Installer().PrepareAsync(Offer(zip), "1.0.0", default);
        _server.Requests.Clear();
        var second = await Installer().PrepareAsync(Offer(zip), "1.0.0", default);
        Assert.True(second.Ok);
        Assert.Equal(first.Dir, second.Dir);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task A_lost_connection_or_a_full_disk_is_not_the_versions_fault()
    {
        var zip = GoodZip();
        _server.Down = true;
        var offline = await Installer().PrepareAsync(Offer(zip), "1.0.0", default);
        Assert.False(offline.Ok);
        Assert.False(offline.Permanent);
        Assert.False(Bad.IsBad("1.1.0"));                                                               // не плохая версия: попробуем позже
        _server.Down = false;
        var full = await Installer(free: _ => 1000).PrepareAsync(Offer(zip), "1.0.0", default);
        Assert.False(full.Ok);
        Assert.Contains("места", full.Error);
        Assert.False(full.Permanent);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task A_server_that_sends_an_endless_file_is_cut_off_at_the_signed_size()
    {
        var zip = GoodZip();
        _server.OnBytes("GET", "/api/agent/update/package/5", 200, new byte[zip.Length + 5000]);
        var r = await Installer().PrepareAsync(Offer(zip), "1.0.0", default);
        Assert.False(r.Ok);
        Assert.Empty(Directory.GetFiles(Downloads));
        Assert.False(Directory.Exists(Path.Combine(_root, "versions", "1.1.0")));
    }
}

public class DownloadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "clubos-test-dl-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private (ServerApi Api, FakeServer Server) Create()
    {
        var server = new FakeServer();
        return (new ServerApi(new HttpClient(server) { BaseAddress = new Uri("http://club.test") }, FakeServer.PcKey, "machine-123456"), server);
    }

    [Fact]
    public async Task A_file_is_streamed_to_disk_with_its_hash_computed_on_the_fly()
    {
        var (api, server) = Create();
        var data = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 7)).ToArray();
        server.OnBytes("GET", "/api/agent/update/package/1", 200, data);
        var dest = Path.Combine(_root, "a", "pkg.zip");
        var r = await api.DownloadToFileAsync("/api/agent/update/package/1", dest, 1_000_000);
        Assert.True(r.Ok);
        Assert.Equal(data.Length, r.Value.Size);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(), r.Value.Sha256);
        Assert.Equal(data, File.ReadAllBytes(dest));
        Assert.Equal(FakeServer.PcKey, server.Requests[0].PcKey);
        Assert.Equal("machine-123456", server.Requests[0].MachineId);
    }

    [Fact]
    public async Task Too_big_missing_or_unreachable_downloads_fail_cleanly_and_leave_no_file()
    {
        var (api, server) = Create();
        var dest = Path.Combine(_root, "pkg.zip");
        server.OnBytes("GET", "/big", 200, new byte[5000]);
        var big = await api.DownloadToFileAsync("/big", dest, 1000);
        Assert.False(big.Ok);
        Assert.False(File.Exists(dest));
        server.On("GET", "/missing", 404, Samples.Error("Версия недоступна"));
        var missing = await api.DownloadToFileAsync("/missing", dest, 1000);
        Assert.Equal(404, missing.Status);
        Assert.Equal("Версия недоступна", missing.Error);
        server.Down = true;
        var offline = await api.DownloadToFileAsync("/big", dest, 1000);
        Assert.True(offline.NetworkError);
        Assert.False(File.Exists(dest));
    }
}
