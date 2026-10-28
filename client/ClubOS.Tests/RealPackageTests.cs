using ClubOS.Service.Update;

namespace ClubOS.Tests;

/// <summary>
/// Проверка на настоящей сборке: если рядом лежит свежая публикация (client\dist после publish.ps1 -Zip), настоящий валидатор службы должен её принять.
/// Этот тест нашёл ошибку, которую не видели тесты на подделках: версия читалась из ресурсов exe средствами Windows, а они не работают на путях длиннее 260 знаков.
/// Без публикации тесты ничего не делают.
/// </summary>
public class RealPackageTests
{
    private static string? DistDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "dist", "Service")) && File.Exists(Path.Combine(dir.FullName, "dist", "version.txt")))
                return Path.Combine(dir.FullName, "dist");
        return null;
    }

    [Fact]
    public void A_real_publish_output_passes_the_package_validator()
    {
        var dist = DistDir();
        if (dist is null) return;
        var version = File.ReadAllText(Path.Combine(dist, "version.txt")).Trim();
        var problem = new PackageValidator().Validate(dist, version);
        Assert.True(problem is null, problem);
    }

    [Fact]
    public void A_real_zip_unpacked_deep_in_a_long_path_still_validates()
    {
        var dist = DistDir();
        if (dist is null) return;
        var version = File.ReadAllText(Path.Combine(dist, "version.txt")).Trim();
        var zip = Path.Combine(dist, $"ClubOS-{version}.zip");
        if (!File.Exists(zip)) return;
        var temp = Path.Combine(Path.GetTempPath(), "clubos-test-realzip-" + Guid.NewGuid().ToString("N"), new string('d', 100), new string('e', 100));   // заведомо длиннее 260 знаков
        try
        {
            Assert.Null(SafeUnzip.Extract(zip, temp));
            var problem = new PackageValidator().Validate(temp, version);
            Assert.True(problem is null, problem);
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(temp))!, true); } catch (IOException) { } }
    }
}
