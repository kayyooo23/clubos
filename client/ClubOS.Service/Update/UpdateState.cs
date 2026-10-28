using System.Text.Json;
using ClubOS.Shared;

namespace ClubOS.Service.Update;

/// <summary>Что служба узнаёт от сервера о доступной версии (GET /api/agent/update).</summary>
public sealed record UpdateOffer(int Id, string Version, string Sha256, long Size, string Signature, string? Notes = null, string? Url = null);

public sealed record UpdateCheckDto(UpdateOffer? Update);

/// <summary>Задание помощнику, который переключает версию: откуда, куда, какие параметры службы и задачи менять и сколько ждать, пока новая версия подтвердит работу.</summary>
public sealed record UpdatePlan(string FromVersion, string ToVersion, string FromDir, string ToDir, string HealthFile, int TimeoutSeconds = 180,
    string ServiceName = "ClubOSAgent", string TaskName = "ClubOS Overlay");

/// <summary>
/// Где обновление сейчас. switching: помощник переключает версию, новая должна подтвердить работу; ok: новая версия подтвердила;
/// rolled_back: новая не заработала, возвращена прежняя; failed: откат не удался. Reported: итог уже отправлен серверу.
/// </summary>
public sealed record UpdateState(string State, string From, string To, long At, string? Message = null, bool Reported = false);

/// <summary>Файл «состояние обновления». Пишется целиком через временный файл, чтобы обрыв питания не оставил половину.</summary>
public sealed class UpdateStateStore(string path)
{
    public UpdateState? Load()
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(path), Json.Options) : null; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public void Save(UpdateState? state)
    {
        try
        {
            if (state is null) { File.Delete(path); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, Json.Options));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* не записалось: следующий шаг попробует снова */ }
    }
}

/// <summary>
/// Версии, которые не заработали или не прошли проверку: их не предлагаем себе снова несколько часов. Не навсегда: причиной мог быть временный сбой
/// (связь в момент запуска), а выпущенную исправленную версию сервер всё равно предложит под новым номером.
/// </summary>
public sealed class BadVersions(string path, TimeProvider? time = null)
{
    public static readonly TimeSpan Remember = TimeSpan.FromHours(6);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private Dictionary<string, long> Read()
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(path)) ?? [] : []; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    public bool IsBad(string version) => Read().TryGetValue(version, out var at) && _time.GetUtcNow().ToUnixTimeSeconds() - at < Remember.TotalSeconds;

    public void Add(string version)
    {
        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        var rows = Read().Where(p => now - p.Value < Remember.TotalSeconds).ToDictionary(p => p.Key, p => p.Value);
        rows[version] = now;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(rows));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* необязательная память: без неё версия просто предложится снова */ }
    }
}

/// <summary>Отметка «новая версия работает»: файл, который новая служба пишет, когда вышла на связь с сервером; помощник ждёт его появления.</summary>
public static class HealthFile
{
    public static void Write(string path, string version, DateTimeOffset now)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new Health(version, now.ToUnixTimeSeconds()), Json.Options));
    }

    /// <summary>Файл есть, написан версией version и не раньше since (отметка от прошлой попытки не считается).</summary>
    public static bool IsHealthy(string path, string version, DateTimeOffset since)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var h = JsonSerializer.Deserialize<Health>(File.ReadAllText(path), Json.Options);
            return h is not null && h.Version == version && h.At >= since.ToUnixTimeSeconds();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return false; }
    }

    private sealed record Health(string Version, long At);
}
