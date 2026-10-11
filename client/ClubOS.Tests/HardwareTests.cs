using System.Text.Json;
using System.Text.RegularExpressions;
using ClubOS.Service;
using ClubOS.Shared;

namespace ClubOS.Tests;

public class HardwareTests
{
    [Fact]
    public void Mac_is_formatted_in_the_form_the_server_accepts()
    {
        var mac = HardwareReport.FormatMac([0x00, 0x1F, 0x16, 0xDD, 0x4D, 0xCB]);
        Assert.Equal("00-1F-16-DD-4D-CB", mac);
        Assert.Matches(@"^([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}$", mac!);
        Assert.Null(HardwareReport.FormatMac([1, 2, 3]));
        Assert.Null(HardwareReport.FormatMac(null));
    }

    [Theory]
    [InlineData(17_179_869_184L, 16)]     // 16 ГБ
    [InlineData(16_977_000_000L, 16)]     // Windows показывает чуть меньше: округляем до привычного числа
    [InlineData(8_000_000_000L, 7)]
    [InlineData(0L, 0)]
    public void Memory_is_rounded_to_whole_gigabytes(long bytes, int expected) => Assert.Equal(expected, HardwareReport.ToGb(bytes));

    [Fact]
    public void Report_is_clipped_to_the_server_limits()
    {
        var report = new HardwareReport(new string('x', 500), 16, "  ", new string('o', 300), new string('1', 100), "bad-mac",
            Enumerable.Range(0, 12).Select(i => new DiskReport($"Disk-number-{i}-with-long-name", 100, 50)).ToList()).Normalized();
        Assert.Equal(120, report.Cpu!.Length);
        Assert.Null(report.Gpu);                          // пустая строка = неизвестно
        Assert.Equal(100, report.Os!.Length);
        Assert.Equal(45, report.Ip!.Length);
        Assert.Null(report.Mac);                          // не MAC: лучше ничего, чем отказ сервера
        Assert.Equal(8, report.Disks.Count);
        Assert.All(report.Disks, d => Assert.True(d.Name.Length <= 16));
    }

    [Fact]
    public void Report_is_sent_in_the_snake_case_the_server_expects()
    {
        var json = JsonSerializer.Serialize(new HardwareReport("i5", 16, "RTX", "Windows 11", "192.168.1.5", "00-1F-16-DD-4D-CB",
            [new("C:", 476, 200)]), Json.Options);
        Assert.Contains("\"ram_gb\":16", json);
        Assert.Contains("\"total_gb\":476", json);
        Assert.Contains("\"free_gb\":200", json);
        Assert.DoesNotContain("RamGb", json);
    }

    [Fact]
    public void Collecting_on_this_machine_gives_a_report_the_server_would_accept()
    {
        var report = HardwareCollector.Collect();
        Assert.True(report.RamGb > 0, "объём памяти должен определяться");
        Assert.NotEmpty(report.Disks);
        Assert.All(report.Disks, d => Assert.InRange(d.TotalGb, 1, 1_000_000));
        if (report.Mac is not null) Assert.Matches(new Regex(@"^([0-9A-F]{2}-){5}[0-9A-F]{2}$"), report.Mac);
        Assert.True((report.Cpu?.Length ?? 0) <= 120 && (report.Gpu?.Length ?? 0) <= 120);
        Assert.DoesNotContain("  ", report.Cpu ?? "");
    }

    [Fact]
    public async Task Report_goes_to_the_server_with_the_pc_key_and_failure_is_reported()
    {
        var server = new FakeServer().On("POST", "/api/agent/hardware", 200, """{"ok":true,"changed":false}""");
        var http = new HttpClient(server) { BaseAddress = new Uri("http://club.test") };
        var api = new ServerApi(http, FakeServer.PcKey);
        var report = new HardwareReport("i5", 16, null, null, null, null, []);
        Assert.True(await HardwareReporter.ReportOnceAsync(api, report, CancellationToken.None));
        var seen = server.To("/api/agent/hardware").Single();
        Assert.Equal(FakeServer.PcKey, seen.PcKey);
        Assert.Null(seen.Auth);
        Assert.Contains("\"cpu\":\"i5\"", seen.Body);

        server.Down = true;
        Assert.False(await HardwareReporter.ReportOnceAsync(api, report, CancellationToken.None));
    }
}
