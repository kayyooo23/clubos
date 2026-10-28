using System.Reflection;
using System.Text.RegularExpressions;

namespace ClubOS.Shared;

/// <summary>Версия клиента: от трёх до четырёх чисел через точку («1.4.0»). Служба и оверлей собираются с одной версией (client/Directory.Build.props).</summary>
public static partial class AppVersion
{
    [GeneratedRegex(@"^\d{1,4}\.\d{1,4}\.\d{1,4}(\.\d{1,4})?$")]
    private static partial Regex Pattern();

    public static bool IsValid(string? version) => version is not null && Pattern().IsMatch(version);

    /// <summary>Версия этой программы. Хвост «+отпечаток коммита», который добавляет SDK, отбрасывается; «1.2.3.0» это «1.2.3».</summary>
    public static string Current { get; } = Detect();

    private static string Detect()
    {
        var assembly = typeof(AppVersion).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (Normalize(informational) is { } fromInfo) return fromInfo;
        return Normalize(assembly.GetName().Version?.ToString()) ?? "0.0.0";
    }

    /// <summary>Приводит строку к виду «1.2.3» или «1.2.3.4»; не версия: null.</summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var core = raw.Trim().Split('+', '-')[0];
        if (!IsValid(core)) return null;
        var parts = core.Split('.');
        return parts.Length == 4 && parts[3] == "0" ? string.Join('.', parts[..3]) : core;
    }

    public static int Compare(string a, string b)
    {
        var x = Numbers(a);
        var y = Numbers(b);
        for (var i = 0; i < 4; i++)
        {
            var c = x[i].CompareTo(y[i]);
            if (c != 0) return c;
        }
        return 0;
    }

    public static bool IsNewer(string candidate, string current) => Compare(candidate, current) > 0;

    private static int[] Numbers(string version)
    {
        var result = new int[4];
        var parts = version.Split('.');
        for (var i = 0; i < Math.Min(parts.Length, 4); i++) result[i] = int.TryParse(parts[i], out var n) ? n : 0;
        return result;
    }
}
