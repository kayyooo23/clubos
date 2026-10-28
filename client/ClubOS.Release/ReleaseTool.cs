using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using ClubOS.Shared;

namespace ClubOS.Release;

/// <summary>
/// Инструмент владельца продукта. Закрытый ключ подписи живёт только у него и нигде больше: ни на сервере, ни в репозитории, ни на клубных ПК.
///   keygen   создать пару ключей (закрытый файл зашифрован паролем; открытый кладётся в client/ClubOS.Service/release-public-key.pem и вшивается в программу);
///   sign     подписать пакет версии (zip из publish.ps1 -Zip), получается файл .release.json со всем, что нужно серверу;
///   verify   проверить подпись так же, как это сделает клиент;
///   upload   завести версию на сервере и загрузить пакет (то же можно сделать в панели платформы).
/// </summary>
public sealed class ReleaseTool(TextWriter output, TextWriter error, Func<string, string?>? env = null, HttpMessageHandler? handler = null, Func<string?>? readPassword = null)
{
    private readonly Func<string, string?> _env = env ?? Environment.GetEnvironmentVariable;

    public static async Task<int> Main(string[] args) => await new ReleaseTool(Console.Out, Console.Error, readPassword: PromptPassword).RunAsync(args);

    private static string? PromptPassword()
    {
        if (Console.IsInputRedirected) return null;
        Console.Error.Write("Пароль закрытого ключа: ");
        var password = new System.Text.StringBuilder();
        while (Console.ReadKey(true) is { } key && key.Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace) { if (password.Length > 0) password.Length--; }
            else password.Append(key.KeyChar);
        }
        Console.Error.WriteLine();
        return password.Length == 0 ? null : password.ToString();
    }

    public async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { Usage(); return args.Length == 0 ? 1 : 0; }
        var options = Parse(args.Skip(1).ToArray());
        try
        {
            return args[0] switch
            {
                "keygen" => KeyGen(options),
                "sign" => Sign(options),
                "verify" => Verify(options),
                "upload" => await UploadAsync(options).ConfigureAwait(false),
                _ => Fail($"Неизвестная команда «{args[0]}»"),
            };
        }
        catch (Exception e) when (e is IOException or CryptographicException or InvalidDataException or JsonException or HttpRequestException or ArgumentException or UnauthorizedAccessException)
        {
            return Fail(e.Message);
        }
    }

    private void Usage()
    {
        output.WriteLine("""
            ClubOS.Release: подпись и выпуск версий клиента

              keygen --private <файл> --public <файл> [--password-env ИМЯ | --no-password]
              sign   --package <zip> --key <файл> [--password-env ИМЯ] [--notes "что нового"] [--out <файл.release.json>]
              verify --package <zip> --release <файл.release.json> --public <файл>
              upload --server <адрес> --platform-key <ключ> --package <zip> --release <файл.release.json> [--percent N] [--clubs 1,2] [--activate]

            Пример: dotnet run --project client/ClubOS.Release -- sign --package client/dist/ClubOS-1.2.0.zip --key release-signing.key
            """);
    }

    private int Fail(string message) { error.WriteLine("Ошибка: " + message); return 1; }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) throw new ArgumentException($"Лишний аргумент «{args[i]}»");
            var name = args[i][2..];
            map[name] = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "true";
        }
        return map;
    }

    private static string Need(Dictionary<string, string> o, string name) =>
        o.TryGetValue(name, out var v) && v != "true" ? v : throw new ArgumentException($"Нужен параметр --{name}");

    private string? Password(Dictionary<string, string> o)
    {
        if (o.TryGetValue("password-env", out var name)) return _env(name) is { Length: > 0 } p ? p : throw new ArgumentException($"Переменная окружения {name} пуста");
        return readPassword?.Invoke();
    }

    // ---------- keygen ----------
    private int KeyGen(Dictionary<string, string> o)
    {
        var privatePath = Need(o, "private");
        var publicPath = Need(o, "public");
        if (File.Exists(privatePath)) return Fail($"Файл {privatePath} уже есть: существующий ключ не перезаписываем (потерянный закрытый ключ нельзя восстановить)");
        string? password = null;
        if (!o.ContainsKey("no-password"))
        {
            password = Password(o);
            if (password is null) return Fail("Нужен пароль для закрытого ключа: --password-env ИМЯ или ввод с клавиатуры (либо явно --no-password)");
        }
        using var key = ReleaseSigning.GenerateKey();
        var pem = password is null
            ? key.ExportPkcs8PrivateKeyPem()
            : key.ExportEncryptedPkcs8PrivateKeyPem(password, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 200_000));
        File.WriteAllText(privatePath, pem);
        File.WriteAllText(publicPath, key.ExportSubjectPublicKeyInfoPem());
        output.WriteLine($"Закрытый ключ: {privatePath}");
        output.WriteLine($"Открытый ключ: {publicPath}");
        output.WriteLine("Закрытый ключ храните отдельно от репозитория и сервера (лучше офлайн) и сделайте его копию: без него нельзя выпускать обновления.");
        output.WriteLine("Открытый ключ положите в client/ClubOS.Service/release-public-key.pem и пересоберите клиент: он вшивается в программу.");
        return 0;
    }

    private ECDsa LoadPrivate(Dictionary<string, string> o)
    {
        var pem = File.ReadAllText(Need(o, "key"));
        var key = ECDsa.Create();
        if (pem.Contains("ENCRYPTED PRIVATE KEY", StringComparison.Ordinal))
        {
            var password = Password(o) ?? throw new ArgumentException("Ключ зашифрован: нужен пароль (--password-env ИМЯ или ввод с клавиатуры)");
            key.ImportFromEncryptedPem(pem, password);
        }
        else key.ImportFromPem(pem);
        if (key.KeySize != 256) throw new ArgumentException("Нужен ключ ECDSA P-256");
        return key;
    }

    // ---------- sign ----------
    public sealed record SignedRelease(string Version, string Sha256, long Size, string Signature, string? Notes);

    private static (string Version, string Sha256, long Size) Inspect(string package)
    {
        using var zip = ZipFile.OpenRead(package);
        var entry = zip.GetEntry("version.txt") ?? throw new ArgumentException("В пакете нет version.txt: соберите его publish.ps1 -Zip");
        using var reader = new StreamReader(entry.Open());
        var version = AppVersion.Normalize(reader.ReadToEnd()) ?? throw new ArgumentException("В version.txt не версия");
        using var stream = File.OpenRead(package);
        var sha = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return (version, sha, new FileInfo(package).Length);
    }

    private int Sign(Dictionary<string, string> o)
    {
        var package = Need(o, "package");
        var (version, sha, size) = Inspect(package);
        using var key = LoadPrivate(o);
        var signature = ReleaseSigning.Sign(key, version, sha, size);
        using (var pub = ReleaseSigning.LoadPublicKey(key.ExportSubjectPublicKeyInfoPem()))
            if (pub is null || !ReleaseSigning.Verify(pub, version, sha, size, signature)) return Fail("Подпись не прошла собственную проверку");   // страховка от сбоя
        var path = o.TryGetValue("out", out var given) && given != "true" ? given : Path.ChangeExtension(package, ".release.json");
        var release = new SignedRelease(version, sha, size, signature, o.GetValueOrDefault("notes"));
        File.WriteAllText(path, JsonSerializer.Serialize(release, Json.Options));
        output.WriteLine($"Версия {version}, размер {size} байт, sha256 {sha}");
        output.WriteLine($"Подписано: {path}");
        return 0;
    }

    // ---------- verify ----------
    private int Verify(Dictionary<string, string> o)
    {
        var (version, sha, size) = Inspect(Need(o, "package"));
        var release = JsonSerializer.Deserialize<SignedRelease>(File.ReadAllText(Need(o, "release")), Json.Options) ?? throw new JsonException("Пустой файл версии");
        using var pub = ReleaseSigning.LoadPublicKey(File.ReadAllText(Need(o, "public"))) ?? throw new ArgumentException("Это не открытый ключ P-256");
        if (release.Version != version || release.Sha256 != sha || release.Size != size) return Fail("Пакет не совпадает с файлом .release.json (версия, sha256 или размер)");
        if (!ReleaseSigning.Verify(pub, release.Version, release.Sha256, release.Size, release.Signature)) return Fail("Подпись не сходится с этим открытым ключом");
        output.WriteLine($"Подпись верна: версия {version}");
        return 0;
    }

    // ---------- upload ----------
    private async Task<int> UploadAsync(Dictionary<string, string> o)
    {
        var server = Need(o, "server").TrimEnd('/');
        var package = Need(o, "package");
        var release = JsonSerializer.Deserialize<SignedRelease>(File.ReadAllText(Need(o, "release")), Json.Options) ?? throw new JsonException("Пустой файл версии");
        var (version, sha, size) = Inspect(package);
        if (release.Version != version || release.Sha256 != sha || release.Size != size) return Fail("Пакет не совпадает с файлом .release.json: загрузка отменена");
        var clubs = o.TryGetValue("clubs", out var list) && list != "true" ? list.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList() : null;
        using var http = handler is null ? new HttpClient() : new HttpClient(handler, false);
        http.Timeout = TimeSpan.FromMinutes(30);
        http.DefaultRequestHeaders.Add("X-Platform-Key", Need(o, "platform-key"));

        var created = await http.PostAsJsonAsync($"{server}/api/platform/releases",
            new { release.Version, release.Sha256, release.Size, release.Signature, release.Notes, rollout_percent = 0, club_ids = clubs }, Json.Options).ConfigureAwait(false);
        if (!created.IsSuccessStatusCode) return Fail($"Сервер не принял версию: {await created.Content.ReadAsStringAsync().ConfigureAwait(false)}");
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false)).GetProperty("id").GetInt32();

        await using var file = File.OpenRead(package);
        using var body = new StreamContent(file);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        body.Headers.ContentLength = size;
        var uploaded = await http.PutAsync($"{server}/api/platform/releases/{id}/package", body).ConfigureAwait(false);
        if (!uploaded.IsSuccessStatusCode) return Fail($"Пакет не загружен: {await uploaded.Content.ReadAsStringAsync().ConfigureAwait(false)}");
        output.WriteLine($"Версия {version} загружена на сервер (№{id}). Пока она никому не раздаётся.");

        if (o.ContainsKey("activate"))
        {
            var percent = o.TryGetValue("percent", out var p) && int.TryParse(p, out var n) ? n : 100;
            var patched = await http.PatchAsJsonAsync($"{server}/api/platform/releases/{id}", new { active = true, rollout_percent = percent }, Json.Options).ConfigureAwait(false);
            if (!patched.IsSuccessStatusCode) return Fail($"Версия загружена, но не включена: {await patched.Content.ReadAsStringAsync().ConfigureAwait(false)}");
            output.WriteLine($"Версия включена: достаётся {percent}% ПК{(clubs is null ? "" : $" клубов {string.Join(", ", clubs)}")}.");
        }
        else output.WriteLine("Включите её в панели платформы (раздел «Версии клиента») или повторите с --activate --percent N.");
        return 0;
    }
}
