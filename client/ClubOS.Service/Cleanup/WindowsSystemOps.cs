using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace ClubOS.Service.Cleanup;

/// <summary>Настоящие операции Windows для очистки: кто вошёл, какие процессы идут, закрытие процесса, вход в Steam.</summary>
public sealed class WindowsSystemOps : ISystemOps
{
    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);

    private const int WtsUserName = 5, WtsDomainName = 7;

    private static string? Query(uint session, int infoClass)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, session, infoClass, out var buffer, out _)) return null;
        try { return Marshal.PtrToStringUni(buffer); }
        finally { WTSFreeMemory(buffer); }
    }

    public ConsoleUser? GetConsoleUser()
    {
        try
        {
            var session = WTSGetActiveConsoleSessionId();
            if (session == 0xFFFFFFFF) return null;
            var name = Query(session, WtsUserName);
            if (string.IsNullOrEmpty(name)) return null;
            var domain = Query(session, WtsDomainName);
            var account = string.IsNullOrEmpty(domain) ? name : $"{domain}\\{name}";
            var sid = new NTAccount(account).Translate(typeof(SecurityIdentifier)).Value;
            using var key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}");
            var profile = key?.GetValue("ProfileImagePath") as string;
            if (string.IsNullOrEmpty(profile)) return null;
            return new ConsoleUser(name, sid, Environment.ExpandEnvironmentVariables(profile), (int)session);
        }
        catch (Exception e) when (e is IdentityNotMappedException or InvalidOperationException or System.Security.SecurityException) { return null; }
    }

    public IReadOnlyList<ProcessEntry> ListProcesses()
    {
        var list = new List<ProcessEntry>();
        foreach (var p in Process.GetProcesses())
        {
            try { list.Add(new ProcessEntry(p.Id, p.ProcessName, p.SessionId, p.StartTime)); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { /* закрылся или недоступен: не наш случай */ }
            finally { p.Dispose(); }
        }
        return list;
    }

    public bool KillTree(int processId)
    {
        try
        {
            using var p = Process.GetProcessById(processId);
            p.Kill(entireProcessTree: true);
            return p.WaitForExit(3000);
        }
        catch (ArgumentException) { return true; }                       // уже закрылся
        catch (InvalidOperationException) { return true; }
        catch (System.ComponentModel.Win32Exception) { return false; }   // нет прав или защищённый процесс
    }

    /// <summary>Steam хранит вход в профиле гостя (реестр) и в своей папке (loginusers.vdf). Убираем и то и другое.</summary>
    public void ResetSteamLogin(ConsoleUser user, List<string> report)
    {
        try
        {
            using (var key = Registry.Users.OpenSubKey($@"{user.Sid}\Software\Valve\Steam", writable: true))
            {
                if (key is not null)
                {
                    key.SetValue("AutoLoginUser", "", RegistryValueKind.String);
                    key.SetValue("RememberPassword", 0, RegistryValueKind.DWord);
                }
            }
            using var machine = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam") ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
            if (machine?.GetValue("InstallPath") is string dir)
            {
                var vdf = Path.Combine(dir, "config", "loginusers.vdf");
                if (File.Exists(vdf)) File.Delete(vdf);
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            report.Add($"вход в Steam не сброшен: {e.Message}");
        }
    }
}
