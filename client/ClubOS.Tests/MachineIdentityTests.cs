using System.Text;
using ClubOS.Service;
using ClubOS.Shared;

namespace ClubOS.Tests;

/// <summary>Отпечаток компьютера: стабильный для одного железа, разный для разного, без заводских заглушек.</summary>
public class MachineIdentityTests
{
    private const string Uuid = "44332211-6655-8877-99aa-bbccddeeff00";

    [Fact]
    public void The_same_hardware_gives_the_same_short_fingerprint()
    {
        var a = MachineIdentity.Compute(Uuid, "PF3ABCDE", "guid-1");
        Assert.Equal(a, MachineIdentity.Compute(Uuid, "PF3ABCDE", "guid-1"));
        Assert.Matches("^[0-9a-f]{32}$", a);                              // подходит под правило сервера для machine_id
    }

    [Fact]
    public void Different_boards_give_different_fingerprints()
    {
        Assert.NotEqual(MachineIdentity.Compute(Uuid, "PF3ABCDE", "g"), MachineIdentity.Compute("11111111-2222-3333-4444-555555555555", "PF3ABCDE", "g"));
        Assert.NotEqual(MachineIdentity.Compute(Uuid, "PF3ABCDE", "g"), MachineIdentity.Compute(Uuid, "PF3XXXXX", "g"));
    }

    [Fact]
    public void Reinstalling_windows_does_not_change_the_fingerprint_when_the_board_is_known()
    {
        Assert.Equal(MachineIdentity.Compute(Uuid, "PF3ABCDE", "guid-before"), MachineIdentity.Compute(Uuid, "PF3ABCDE", "guid-after"));
        Assert.Equal(MachineIdentity.Compute(Uuid.ToUpperInvariant(), "  PF3ABCDE ", null), MachineIdentity.Compute(Uuid, "PF3ABCDE", null));
    }

    [Fact]
    public void Factory_placeholders_are_ignored_and_the_os_guid_is_used_instead()
    {
        var fallback = MachineIdentity.Compute("03000200-0400-0500-0006-000700080009", "To be filled by O.E.M.", "guid-1");
        Assert.Equal(fallback, MachineIdentity.Compute("ffffffff-ffff-ffff-ffff-ffffffffffff", "Default string", "guid-1"));
        Assert.NotEqual(fallback, MachineIdentity.Compute("00000000-0000-0000-0000-000000000000", "0000000", "guid-2"));   // без железа отпечаток зависит от установки Windows
    }

    [Theory]
    [InlineData("PF3ABCDE", true)]
    [InlineData("  S/N 12345  ", true)]
    [InlineData("To be filled by O.E.M.", false)]
    [InlineData("Default string", false)]
    [InlineData("None", false)]
    [InlineData("0000000", false)]
    [InlineData("ab", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Serial_numbers_are_judged_by_whether_they_can_tell_boards_apart(string? value, bool usable) =>
        Assert.Equal(usable, MachineIdentity.UsableSerial(value));

    [Fact]
    public void Detect_works_on_this_computer_and_is_stable()
    {
        var first = MachineIdentity.Detect();
        Assert.Matches("^[0-9a-f]{32}$", first);
        Assert.Equal(first, MachineIdentity.Detect());
    }

    // ---------- таблица SMBIOS ----------
    private static byte[] Structure(byte type, int formattedLength, Action<byte[]> fill, params string[] strings)
    {
        var body = new byte[formattedLength];
        body[0] = type;
        body[1] = (byte)formattedLength;
        fill(body);
        var text = strings.Length == 0 ? new byte[] { 0, 0 } : Encoding.ASCII.GetBytes(string.Join('\0', strings) + "\0\0");
        return [.. body, .. text];
    }

    private static byte[] Table(params byte[][] structures)
    {
        var content = structures.SelectMany(s => s).ToArray();
        var header = new byte[8];
        header[1] = 3; header[2] = 3;
        BitConverter.GetBytes((uint)content.Length).CopyTo(header, 4);
        return [.. header, .. content];
    }

    private static readonly byte[] UuidBytes = [0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x00];

    private static byte[] Bios() => Structure(0, 0x18, b => { b[4] = 1; b[5] = 2; }, "ACME BIOS", "1.0");
    private static byte[] SystemInfo() => Structure(1, 0x1B, b => { b[4] = 1; b[5] = 2; b[6] = 3; b[7] = 4; UuidBytes.CopyTo(b, 8); }, "ACME", "Box", "v1", "SYS-0001");
    private static byte[] Board() => Structure(2, 0x0F, b => { b[4] = 1; b[5] = 2; b[6] = 3; b[7] = 4; }, "MSI", "B550", "1.0", "BSN-998877");
    private static byte[] End() => Structure(127, 4, _ => { });

    [Fact]
    public void Reads_the_uuid_and_the_board_serial_from_a_real_shaped_table()
    {
        var (uuid, board) = Smbios.Parse(Table(Bios(), SystemInfo(), Board(), End()));
        Assert.Equal(Uuid, uuid);
        Assert.Equal("BSN-998877", board);
    }

    [Fact]
    public void Structures_without_strings_do_not_confuse_the_reader()
    {
        var (uuid, board) = Smbios.Parse(Table(Structure(9, 6, _ => { }), SystemInfo(), Structure(10, 5, _ => { }), Board(), End()));
        Assert.Equal(Uuid, uuid);
        Assert.Equal("BSN-998877", board);
    }

    [Fact]
    public void A_table_without_the_needed_records_gives_nothing_and_a_cut_table_does_not_crash()
    {
        Assert.Equal((null, null), Smbios.Parse(Table(Bios(), End())));
        Assert.Equal((null, null), Smbios.Parse([]));
        var cut = Table(Bios(), SystemInfo(), Board());
        for (var length = 8; length < cut.Length; length += 7)
            _ = Smbios.Parse(cut[..length]);                              // не бросает исключений на обрезанных данных
        var (uuid, _) = Smbios.Parse(Table(Bios(), SystemInfo(), Board())[..(8 + Bios().Length + SystemInfo().Length)]);
        Assert.Equal(Uuid, uuid);
    }
}

public class ServerApiEnrollmentTests
{
    private static (ServerApi Api, FakeServer Server) Create(string? key, string? machine)
    {
        var server = new FakeServer();
        return (new ServerApi(new HttpClient(server) { BaseAddress = new Uri("http://club.test") }, key, machine), server);
    }

    [Fact]
    public async Task The_machine_fingerprint_is_sent_next_to_the_key_and_the_key_may_come_later()
    {
        var (api, server) = Create(null, "abcdef0123456789");
        server.On("GET", "/api/agent/state", 200, Samples.Locked);
        Assert.False(api.HasKey);
        await api.GetAsync<ServerState>("/api/agent/state");
        Assert.Null(server.Requests[0].PcKey);                            // ключа ещё нет: заголовок не отправляется вовсе
        Assert.Equal("abcdef0123456789", server.Requests[0].MachineId);
        api.SetKey("late-key");
        await api.GetAsync<ServerState>("/api/agent/state");
        Assert.Equal("late-key", server.Requests[1].PcKey);
        api.SetKey(null);
        Assert.False(api.HasKey);
    }

    [Fact]
    public async Task The_error_code_from_the_server_is_exposed_next_to_the_text()
    {
        var (api, server) = Create("k", "m");
        server.On("GET", "/api/agent/state", 409, """{"detail":"Привязан к другому","code":"machine_mismatch"}""")
              .On("GET", "/api/agent/tariffs", 403, Samples.Error("Нельзя"));
        var mismatch = await api.GetAsync<ServerState>("/api/agent/state");
        Assert.Equal(409, mismatch.Status);
        Assert.Equal("machine_mismatch", mismatch.Code);
        Assert.Equal("Привязан к другому", mismatch.Error);
        Assert.Null((await api.GetAsync<List<TariffInfo>>("/api/agent/tariffs")).Code);
    }

    [Fact]
    public void Config_accepts_a_code_instead_of_a_key_but_still_needs_the_server()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clubos-test-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "agent.json");
            File.WriteAllText(path, """{"server":"https://club.test/","enroll_code":" K7M2-9QXA-4TPD "}""");
            var cfg = AgentConfig.Load(path);
            Assert.Equal("https://club.test", cfg.Server);
            Assert.Null(cfg.PcKey);
            Assert.Equal("K7M2-9QXA-4TPD", cfg.EnrollCode);

            File.WriteAllText(path, """{"server":"https://club.test","pc_key":"","enroll_code":""}""");
            cfg = AgentConfig.Load(path);                                 // пустые значения считаются отсутствующими; ключ мог быть получен раньше
            Assert.Null(cfg.PcKey);
            Assert.Null(cfg.EnrollCode);

            File.WriteAllText(path, """{"enroll_code":"K7M2-9QXA-4TPD"}""");
            Assert.Throws<InvalidOperationException>(() => AgentConfig.Load(path));
        }
        finally { Directory.Delete(dir, true); }
    }
}
