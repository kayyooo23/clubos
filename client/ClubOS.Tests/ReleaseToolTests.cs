using System.Security.Cryptography;
using System.Text.Json;
using ClubOS.Release;
using ClubOS.Shared;

namespace ClubOS.Tests;

/// <summary>Инструмент владельца: от создания ключей до загрузки версии на сервер. Подпись, которую он выдаёт, должна проверяться тем же кодом, что и в клиенте.</summary>
public class ReleaseToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "clubos-test-tool-" + Guid.NewGuid().ToString("N"));
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public ReleaseToolTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private string P(string name) => Path.Combine(_root, name);

    private ReleaseTool Tool(Dictionary<string, string>? env = null, HttpMessageHandler? handler = null, string? typed = null) =>
        new(_out, _err, name => env is not null && env.TryGetValue(name, out var v) ? v : null, handler, () => typed);

    private string MakePackage(string version = "1.2.0")
    {
        var path = P($"ClubOS-{version}.zip");
        File.WriteAllBytes(path, SafeUnzipTests.Zip(("Service/ClubOS.Service.exe", "s"), ("Overlay/ClubOS.Overlay.exe", "o"), ("version.txt", version)));
        return path;
    }

    private async Task<int> Run(ReleaseTool tool, params string[] args) => await tool.RunAsync(args);

    [Fact]
    public async Task Keys_are_created_signed_with_and_verified_end_to_end()
    {
        var env = new Dictionary<string, string> { ["RELEASE_KEY_PASSWORD"] = "pa55-word" };
        Assert.Equal(0, await Run(Tool(env), "keygen", "--private", P("k.key"), "--public", P("k.pub"), "--password-env", "RELEASE_KEY_PASSWORD"));
        Assert.Contains("ENCRYPTED PRIVATE KEY", File.ReadAllText(P("k.key")));                       // закрытый ключ на диске зашифрован
        Assert.Contains("BEGIN PUBLIC KEY", File.ReadAllText(P("k.pub")));
        var package = MakePackage();
        Assert.Equal(0, await Run(Tool(env), "sign", "--package", package, "--key", P("k.key"), "--password-env", "RELEASE_KEY_PASSWORD", "--notes", "Исправления"));
        var release = JsonSerializer.Deserialize<ReleaseTool.SignedRelease>(File.ReadAllText(Path.ChangeExtension(package, ".release.json")), Json.Options)!;
        Assert.Equal("1.2.0", release.Version);                                                        // версия взята из самого пакета
        Assert.Equal(new FileInfo(package).Length, release.Size);
        Assert.Equal("Исправления", release.Notes);
        using var pub = ReleaseSigning.LoadPublicKey(File.ReadAllText(P("k.pub")))!;                    // как это сделает клиент
        Assert.True(ReleaseSigning.Verify(pub, release.Version, release.Sha256, release.Size, release.Signature));
        Assert.Equal(0, await Run(Tool(), "verify", "--package", package, "--release", Path.ChangeExtension(package, ".release.json"), "--public", P("k.pub")));
    }

    [Fact]
    public async Task A_wrong_password_or_a_changed_package_or_a_foreign_key_is_caught()
    {
        var env = new Dictionary<string, string> { ["PW"] = "right", ["BAD"] = "wrong" };
        await Run(Tool(env), "keygen", "--private", P("k.key"), "--public", P("k.pub"), "--password-env", "PW");
        var package = MakePackage();
        Assert.Equal(1, await Run(Tool(env), "sign", "--package", package, "--key", P("k.key"), "--password-env", "BAD"));
        Assert.Equal(0, await Run(Tool(env), "sign", "--package", package, "--key", P("k.key"), "--password-env", "PW"));
        var json = Path.ChangeExtension(package, ".release.json");
        File.AppendAllText(package, "x");                                                               // пакет изменили после подписи
        Assert.Equal(1, await Run(Tool(), "verify", "--package", package, "--release", json, "--public", P("k.pub")));
        Assert.Contains("не совпадает", _err.ToString());
        using var other = ReleaseSigning.GenerateKey();
        File.WriteAllText(P("other.pub"), other.ExportSubjectPublicKeyInfoPem());
        var fresh = MakePackage("1.3.0");
        await Run(Tool(env), "sign", "--package", fresh, "--key", P("k.key"), "--password-env", "PW");
        Assert.Equal(1, await Run(Tool(), "verify", "--package", fresh, "--release", Path.ChangeExtension(fresh, ".release.json"), "--public", P("other.pub")));
    }

    [Fact]
    public async Task An_existing_private_key_is_never_overwritten_and_a_key_needs_a_password_unless_declined()
    {
        var env = new Dictionary<string, string> { ["PW"] = "pw" };
        Assert.Equal(0, await Run(Tool(env), "keygen", "--private", P("k.key"), "--public", P("k.pub"), "--password-env", "PW"));
        var before = File.ReadAllText(P("k.key"));
        Assert.Equal(1, await Run(Tool(env), "keygen", "--private", P("k.key"), "--public", P("k2.pub"), "--password-env", "PW"));
        Assert.Equal(before, File.ReadAllText(P("k.key")));
        Assert.False(File.Exists(P("k2.pub")));
        Assert.Equal(1, await Run(Tool(), "keygen", "--private", P("n.key"), "--public", P("n.pub")));                 // ни пароля, ни ввода: без ключа
        Assert.False(File.Exists(P("n.key")));
        Assert.Equal(0, await Run(Tool(), "keygen", "--private", P("u.key"), "--public", P("u.pub"), "--no-password"));
        Assert.DoesNotContain("ENCRYPTED", File.ReadAllText(P("u.key")));
        Assert.Equal(0, await Run(Tool(typed: "typed-secret"), "keygen", "--private", P("t.key"), "--public", P("t.pub")));   // пароль введён с клавиатуры
        Assert.Contains("ENCRYPTED", File.ReadAllText(P("t.key")));
    }

    [Fact]
    public async Task A_package_without_a_version_stamp_cannot_be_signed()
    {
        await Run(Tool(), "keygen", "--private", P("k.key"), "--public", P("k.pub"), "--no-password");
        var path = P("bad.zip");
        File.WriteAllBytes(path, SafeUnzipTests.Zip(("Service/ClubOS.Service.exe", "s")));
        Assert.Equal(1, await Run(Tool(), "sign", "--package", path, "--key", P("k.key")));
        Assert.Contains("version.txt", _err.ToString());
        Assert.Equal(1, await Run(Tool(), "frobnicate"));
        Assert.Equal(1, await Run(Tool(), "sign", "--package", path));                                  // нет --key
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string Body, long? Length, string? Key)> Calls { get; } = [];
        public int RejectPackageWith { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : request.Content.Headers.ContentType?.MediaType == "application/zip" ? "<zip>" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.Method, request.RequestUri!.AbsolutePath, body, request.Content?.Headers.ContentLength, request.Headers.GetValues("X-Platform-Key").First()));
            if (request.RequestUri.AbsolutePath.EndsWith("/package") && RejectPackageWith != 0)
                return new HttpResponseMessage((System.Net.HttpStatusCode)RejectPackageWith) { Content = new StringContent("""{"detail":"не совпадает"}""") };
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("""{"id":7}""", System.Text.Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task Upload_creates_the_version_sends_the_package_and_can_switch_it_on_gradually()
    {
        await Run(Tool(), "keygen", "--private", P("k.key"), "--public", P("k.pub"), "--no-password");
        var package = MakePackage();
        await Run(Tool(), "sign", "--package", package, "--key", P("k.key"));
        var handler = new RecordingHandler();
        var code = await Run(Tool(handler: handler), "upload", "--server", "http://club.test/", "--platform-key", "secret-key", "--package", package,
            "--release", Path.ChangeExtension(package, ".release.json"), "--activate", "--percent", "10", "--clubs", "1,2");
        Assert.Equal(0, code);
        Assert.Equal(["POST /api/platform/releases", "PUT /api/platform/releases/7/package", "PATCH /api/platform/releases/7"], handler.Calls.Select(c => $"{c.Method} {c.Path}"));
        Assert.All(handler.Calls, c => Assert.Equal("secret-key", c.Key));
        Assert.Equal(new FileInfo(package).Length, handler.Calls[1].Length);
        using var created = JsonDocument.Parse(handler.Calls[0].Body);
        Assert.Equal("1.2.0", created.RootElement.GetProperty("version").GetString());
        Assert.Equal(0, created.RootElement.GetProperty("rollout_percent").GetInt32());                  // сначала версия заводится выключенной
        Assert.Equal([1, 2], created.RootElement.GetProperty("club_ids").EnumerateArray().Select(x => x.GetInt32()));
        using var patched = JsonDocument.Parse(handler.Calls[2].Body);
        Assert.Equal(10, patched.RootElement.GetProperty("rollout_percent").GetInt32());
        Assert.True(patched.RootElement.GetProperty("active").GetBoolean());
    }

    [Fact]
    public async Task Upload_without_activate_leaves_the_version_off_and_a_refused_package_fails_loudly()
    {
        await Run(Tool(), "keygen", "--private", P("k.key"), "--public", P("k.pub"), "--no-password");
        var package = MakePackage();
        await Run(Tool(), "sign", "--package", package, "--key", P("k.key"));
        var release = Path.ChangeExtension(package, ".release.json");
        var quiet = new RecordingHandler();
        Assert.Equal(0, await Run(Tool(handler: quiet), "upload", "--server", "http://club.test", "--platform-key", "k", "--package", package, "--release", release));
        Assert.Equal(2, quiet.Calls.Count);                                                             // версия заведена, но не включена
        Assert.Contains("Пока она никому не раздаётся", _out.ToString());
        var refusing = new RecordingHandler { RejectPackageWith = 422 };
        Assert.Equal(1, await Run(Tool(handler: refusing), "upload", "--server", "http://club.test", "--platform-key", "k", "--package", package, "--release", release, "--activate"));
        Assert.DoesNotContain(refusing.Calls, c => c.Method == HttpMethod.Patch);                          // не загрузилось: не включаем
        File.AppendAllText(package, "x");
        var tampered = new RecordingHandler();
        Assert.Equal(1, await Run(Tool(handler: tampered), "upload", "--server", "http://club.test", "--platform-key", "k", "--package", package, "--release", release));
        Assert.Empty(tampered.Calls);                                                                    // пакет не тот, что подписан: на сервер даже не идём
    }
}
