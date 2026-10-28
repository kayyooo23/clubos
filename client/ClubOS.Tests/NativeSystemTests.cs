using ClubOS.Overlay;
using ClubOS.Service;
using ClubOS.Shared.Native;

namespace ClubOS.Tests;

/// <summary>
/// Нативная часть (C++, client/ClubOS.Native) и прежний код на C# должны давать одно и то же: перехват клавиш, окна и характеристики ПК.
/// Если библиотека не собрана (на ПК нет компилятора C++), проверки пропускаются: программы в этом случае работают на C#.
/// Тесты ничего не нажимают и не закрывают, окна пользователя не трогают.
/// </summary>
public class NativeSystemTests
{
    // ---------- перехват клавиш ----------
    [Fact]
    public void The_native_key_rule_equals_the_managed_rule_for_every_key_and_modifier()
    {
        if (!NativeKiosk.IsAvailable) return;
        foreach (var (profile, managed) in new[] { (NativeKiosk.Lock, KioskProfile.Lock), (NativeKiosk.Session, KioskProfile.Session) })
            for (var vk = 0; vk < 256; vk++)
                foreach (var alt in new[] { false, true })
                    foreach (var ctrl in new[] { false, true })
                        foreach (var shift in new[] { false, true })
                            Assert.True(KioskGuard.ShouldBlock(vk, alt, ctrl, shift, managed) == NativeKiosk.ShouldBlock(vk, alt, ctrl, shift, profile),
                                $"vk={vk} alt={alt} ctrl={ctrl} shift={shift} профиль={managed}");
    }

    [Fact]
    public void The_kiosk_guard_turns_on_and_off_through_the_native_hook()
    {
        if (!NativeKiosk.IsAvailable) return;
        bool? on = null, afterSwitch = null, off = null;
        var thread = new Thread(() =>              // хук привязан к потоку, который его поставил: ставим и снимаем в одном
        {
            using var guard = new KioskGuard(allowExit: false);
            guard.Enable();
            on = guard.Active;
            guard.Enable(KioskProfile.Session);     // смена набора, хук тот же
            afterSwitch = guard.Active;
            guard.Disable();
            off = guard.Active;
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.Equal((true, true, false), (on, afterSwitch, off));
        Assert.False(NativeKiosk.Active);
    }

    // ---------- окна ----------
    [Fact]
    public void Native_and_managed_window_lists_describe_the_same_windows_the_same_way()
    {
        if (!NativeWindowApi.IsAvailable) return;
        var native = NativeWindowApi.TopLevel();
        var managed = NativeWindows.TopLevelManaged().ToDictionary(w => w.Handle);
        Assert.All(native, w => Assert.True(w.Handle != 0 && w.ProcessId > 0 && w.ClassName.Length > 0));
        var common = 0;
        foreach (var w in native)
        {
            if (!managed.TryGetValue(w.Handle, out var other)) continue;      // окно могло открыться или закрыться между двумя опросами
            common++;
            Assert.Equal(other.ProcessId, w.ProcessId);
            Assert.Equal(other.ClassName, w.ClassName);
            Assert.Equal(other.ProcessName, w.ProcessName);
        }
        if (native.Count > 3) Assert.True(common > 0, "ни одного общего окна у двух опросов подряд");
    }

    [Fact]
    public void Native_and_managed_notification_state_and_foreground_agree()
    {
        if (!NativeWindowApi.IsAvailable) return;
        Assert.InRange(NativeWindowApi.NotificationState(), 1, 7);
        Assert.Equal(NativeWindows.NotificationStateManaged(), NativeWindowApi.NotificationState());
        var native = NativeWindowApi.Foreground();
        var managed = NativeWindows.ForegroundManaged();
        if (native is not null && managed is not null && native.Handle == managed.Handle)
            Assert.Equal((managed.ProcessId, managed.ClassName, managed.ProcessName), (native.ProcessId, native.ClassName, native.ProcessName));
    }

    [Fact]
    public void Native_commands_on_a_missing_window_do_nothing()
    {
        if (!NativeWindowApi.IsAvailable) return;
        NativeWindowApi.Close(0x7FFFFFF0);
        NativeWindowApi.BringToFront(0x7FFFFFF0);
    }

    // ---------- характеристики ПК ----------
    [Fact]
    public void The_native_hardware_report_matches_the_managed_one()
    {
        if (!NativeHardware.IsAvailable) return;
        var native = NativeHardware.Collect();
        Assert.NotNull(native);
        var managed = HardwareCollector.CollectManaged();

        Assert.Equal(managed.Cpu, native!.Cpu);                                         // оба берут название у процессора
        Assert.Equal(managed.Gpu, native.Gpu);                                          // оба берут его из драйверов видеокарт
        Assert.InRange(native.RamGb, 1, 1_000_000);
        Assert.InRange(Math.Abs(native.RamGb - (managed.RamGb ?? 0)), 0, 2);           // установленная память против доступной системе: бывает чуть меньше
        Assert.Equal(managed.Disks.Select(d => (d.Name, d.TotalGb)), native.Disks.Select(d => (d.Name, d.TotalGb)));
        Assert.All(native.Disks, d => Assert.InRange(d.FreeGb, 0, d.TotalGb));
        Assert.Equal(managed.Ip, native.Ip);
        Assert.Equal(managed.Mac, native.Mac);
    }

    [Fact]
    public void The_collector_report_uses_the_native_values_and_still_passes_the_servers_limits()
    {
        var report = HardwareCollector.Collect();
        Assert.True(report.RamGb > 0);
        Assert.NotEmpty(report.Disks);
        Assert.True((report.Cpu?.Length ?? 0) <= 120 && (report.Gpu?.Length ?? 0) <= 120);
        if (NativeHardware.IsAvailable && NativeHardware.Network() is { } net) Assert.Equal(net.Ip, report.Ip);
    }
}
