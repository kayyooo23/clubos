using ClubOS.Service;
using ClubOS.Shared;
using Microsoft.Extensions.Time.Testing;

namespace ClubOS.Tests;

/// <summary>Команды администратора для ПК: что служба выполняет, от чего отказывается и как не допускает повторов.</summary>
public class CommandRunnerTests
{
    private sealed class FakePower : ISystemPower
    {
        public List<(string Kind, int Delay, bool Force)> Calls { get; } = [];
        public (bool Ok, string Message) Answer { get; set; } = (true, "");
        public Task<(bool Ok, string Message)> ScheduleAsync(string kind, int delaySeconds, bool force, CancellationToken ct)
        {
            Calls.Add((kind, delaySeconds, force));
            return Task.FromResult(Answer);
        }
    }

    private sealed class Rig
    {
        public required CommandRunner Runner;
        public required FakeServer Server;
        public required FakePower Power;
        public required MemoryCommandJournal Journal;
        /// <summary>Что служба сообщила серверу по команде. Тело разбираем как JSON: русский текст уходит в нём экранированным.</summary>
        public List<(bool Ok, string Message)> Reports(int id) => Server.To($"/api/agent/commands/{id}/result").Select(r =>
        {
            using var doc = System.Text.Json.JsonDocument.Parse(r.Body);
            return (doc.RootElement.GetProperty("ok").GetBoolean(), doc.RootElement.GetProperty("message").GetString() ?? "");
        }).ToList();
    }

    private static Rig Create()
    {
        var server = new FakeServer();
        var api = new ServerApi(new HttpClient(server) { BaseAddress = new Uri("http://club.test") }, FakeServer.PcKey);
        var power = new FakePower();
        var journal = new MemoryCommandJournal();
        for (var id = 1; id <= 9; id++) server.On("POST", $"/api/agent/commands/{id}/result", 200, """{"status":"done"}""");
        return new Rig { Runner = new CommandRunner(api, power, journal, retryPause: TimeSpan.Zero), Server = server, Power = power, Journal = journal };
    }

    private static CommandInfo Cmd(int id, string kind = "reboot", bool force = false) => new(id, kind, new CommandPayload(force));

    private static AgentSnapshot Snap(PcMode mode, bool session = false) =>
        new(true, mode, null, session ? new SessionInfo(1, 1, 1, "Час", DateTime.UtcNow, null, null, 0, 600) : null, null, session ? 600 : null);

    // ---------- выполнение ----------
    [Fact]
    public async Task A_reboot_is_scheduled_with_a_delay_and_confirmed_to_the_server()
    {
        var rig = Create();
        await rig.Runner.HandleAsync([Cmd(1)], Snap(PcMode.Locked), default);
        Assert.Equal([("reboot", CommandRunner.DelaySeconds, false)], rig.Power.Calls);
        var report = Assert.Single(rig.Reports(1));
        Assert.True(report.Ok);
        Assert.Equal("Перезагрузка через 20 секунд", report.Message);
        Assert.True(rig.Journal.Contains(1));
    }

    [Fact]
    public async Task A_shutdown_says_so()
    {
        var rig = Create();
        await rig.Runner.HandleAsync([Cmd(2, "shutdown")], Snap(PcMode.StaffDesktop), default);   // за рабочим столом сотрудника можно
        Assert.Equal("shutdown", rig.Power.Calls.Single().Kind);
        Assert.Equal("Выключение через 20 секунд", rig.Reports(2).Single().Message);
    }

    [Fact]
    public async Task Commands_run_in_the_order_they_were_given()
    {
        var rig = Create();
        await rig.Runner.HandleAsync([Cmd(3, "shutdown"), Cmd(1, "reboot")], Snap(PcMode.Locked), default);
        Assert.Equal(["reboot", "shutdown"], rig.Power.Calls.Select(c => c.Kind));
    }

    // ---------- отказы ----------
    [Theory]
    [InlineData("format")]
    [InlineData("")]
    [InlineData("REBOOT")]
    public async Task Only_known_commands_are_run(string kind)
    {
        var rig = Create();
        await rig.Runner.HandleAsync([Cmd(4, kind)], Snap(PcMode.Locked), default);
        Assert.Empty(rig.Power.Calls);
        Assert.False(rig.Reports(4).Single().Ok);
    }

    [Fact]
    public async Task A_running_guest_session_blocks_the_command_unless_the_admin_confirmed()
    {
        var rig = Create();
        await rig.Runner.HandleAsync([Cmd(5)], Snap(PcMode.GuestSession, session: true), default);
        Assert.Empty(rig.Power.Calls);
        var report = rig.Reports(5).Single();
        Assert.False(report.Ok);
        Assert.Contains("Идёт сессия гостя", report.Message);
        Assert.False(rig.Journal.Contains(5));                            // отказ не считается выполнением: администратор может подтвердить и повторить

        await rig.Runner.HandleAsync([Cmd(6, force: true)], Snap(PcMode.GuestSession, session: true), default);
        Assert.Equal([("reboot", CommandRunner.DelaySeconds, true)], rig.Power.Calls);
    }

    // ---------- повторная доставка ----------
    [Fact]
    public async Task A_command_delivered_again_after_the_reboot_is_confirmed_but_not_run_twice()
    {
        var rig = Create();
        await rig.Runner.HandleAsync([Cmd(1)], Snap(PcMode.Locked), default);
        rig.Server.Requests.Clear();
        await rig.Runner.HandleAsync([Cmd(1)], Snap(PcMode.Locked), default);   // сервер не получил подтверждение и повторил
        Assert.Single(rig.Power.Calls);
        Assert.True(rig.Reports(1).Single().Ok);
    }

    [Fact]
    public async Task A_failed_attempt_can_be_tried_again()
    {
        var rig = Create();
        rig.Power.Answer = (false, "Отказано в доступе");
        await rig.Runner.HandleAsync([Cmd(1)], Snap(PcMode.Locked), default);
        Assert.Contains("Отказано в доступе", rig.Reports(1).Single().Message);
        Assert.False(rig.Journal.Contains(1));
        rig.Power.Answer = (true, "");
        await rig.Runner.HandleAsync([Cmd(1)], Snap(PcMode.Locked), default);
        Assert.Equal(2, rig.Power.Calls.Count);
    }

    // ---------- отчёт ----------
    [Fact]
    public async Task A_report_lost_on_the_way_is_retried_and_a_final_refusal_is_not()
    {
        var rig = Create();
        var calls = 0;
        rig.Server.On("POST", "/api/agent/commands/7/result", _ => ++calls < 3 ? (500, Samples.Error("сбой")) : (200, """{"status":"done"}"""));
        await rig.Runner.HandleAsync([Cmd(7)], Snap(PcMode.Locked), default);
        Assert.Equal(3, rig.Reports(7).Count);

        rig.Server.On("POST", "/api/agent/commands/8/result", 404, Samples.Error("Команда не найдена"));
        await rig.Runner.HandleAsync([Cmd(8)], Snap(PcMode.Locked), default);
        Assert.Single(rig.Reports(8));                                    // чужая или закрытая команда: повторять нечего
    }

    [Fact]
    public async Task The_trial_mode_confirms_the_command_and_says_the_pc_was_not_touched()
    {
        var server = new FakeServer().On("POST", "/api/agent/commands/1/result", 200, """{"status":"done"}""");
        var runner = new CommandRunner(new ServerApi(new HttpClient(server) { BaseAddress = new Uri("http://club.test") }, FakeServer.PcKey),
            new DryRunSystemPower(), new MemoryCommandJournal());
        await runner.HandleAsync([Cmd(1)], Snap(PcMode.Locked), default);
        using var doc = System.Text.Json.JsonDocument.Parse(server.To("/api/agent/commands/1/result").Single().Body);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("пробный режим", doc.RootElement.GetProperty("message").GetString());
    }

    // ---------- журнал в файле ----------
    private static string TempFile() => Path.Combine(Path.GetTempPath(), "clubos-test-journal-" + Guid.NewGuid().ToString("N") + ".log");

    [Fact]
    public void The_journal_survives_a_restart_and_forgets_after_half_an_hour()
    {
        var path = TempFile();
        try
        {
            var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));
            new FileCommandJournal(path, time).Add(11);
            var after = new FileCommandJournal(path, time);               // служба перезапустилась вместе с ПК
            Assert.True(after.Contains(11));
            Assert.False(after.Contains(12));
            time.Advance(FileCommandJournal.Remember + TimeSpan.FromMinutes(1));
            Assert.False(after.Contains(11));                             // номера могли повториться после пересоздания сервера
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void The_journal_can_forget_a_command_and_never_grows_without_limit()
    {
        var path = TempFile();
        try
        {
            var journal = new FileCommandJournal(path);
            journal.Add(1);
            journal.Forget(1);
            Assert.False(journal.Contains(1));
            for (var i = 1; i <= 300; i++) journal.Add(i);
            Assert.True(File.ReadAllLines(path).Length <= 200);
            Assert.True(journal.Contains(300));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_missing_or_broken_journal_file_is_not_an_error()
    {
        var path = TempFile();
        try
        {
            Assert.False(new FileCommandJournal(path).Contains(1));
            File.WriteAllText(path, "мусор\n1|abc\n|\n");
            Assert.False(new FileCommandJournal(path).Contains(1));
        }
        finally { File.Delete(path); }
    }
}
