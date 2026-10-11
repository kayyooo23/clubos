using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ClubOS.Shared.Ui;

/// <summary>Один баннер, готовый к показу. Meta: рекламодатель и erid (маркировка), если заданы; подпись «РЕКЛАМА» добавляет само окно.</summary>
public sealed record AdSlide(int Id, string Version, string Path, string Meta, int Seconds, int Width, int Height)
{
    public bool HasMeta => Meta.Length > 0;
}

/// <summary>
/// Рекламные баннеры в меню входа. Берёт список у службы раз в минуту (службе нужно время, чтобы скачать картинки, поэтому пока что-то
/// не скачано, спрашивает чаще) и сменяет баннеры по очереди: каждый показывается столько секунд, сколько задал владелец платформы.
/// При сбое оставляет прежние баннеры (экран блокировки не должен терять рекламу из-за секундного обрыва связи).
/// Пустой ответ сервера убирает баннер. Не знает про WPF: плавной сменой занимается представление.
/// Показываем только файлы из папки баннеров с расширением png/jpg: путь приходит по каналу от службы, но проверить его не лишнее.
/// </summary>
public sealed class AdBannerModel : INotifyPropertyChanged
{
    public const int MaxSlides = 12, MinSeconds = 3, MaxSeconds = 120;
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RetryWhileDownloading = TimeSpan.FromSeconds(5);

    private readonly IAgentClient _client;
    private readonly TimeProvider _time;
    private readonly string _dir;
    private readonly HashSet<(int, string)> _broken = [];
    private int _nextId;
    private DateTimeOffset _nextFetch = DateTimeOffset.MinValue, _until;
    private IReadOnlyList<AdSlide> _slides = [];
    private AdSlide? _current;

    public AdBannerModel(IAgentClient client, TimeProvider? time = null, string? adsDir = null)
    {
        _client = client;
        _time = time ?? TimeProvider.System;
        _dir = adsDir ?? AgentConfig.AdsDir;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<AdSlide> Slides => _slides;
    /// <summary>Баннер на экране прямо сейчас. Null, если показывать нечего.</summary>
    public AdSlide? Current { get => _current; private set { if (!Equals(value, _current)) { _current = value; OnChanged(); OnChanged(nameof(HasAds)); } } }
    public bool HasAds => _current is not null;

    /// <summary>Вызывается раз в секунду; сам решает, пора ли спрашивать службу и пора ли менять баннер.</summary>
    public async Task PollAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        if (now >= _nextFetch)
        {
            var r = await _client.SendAsync(new IpcRequest(Interlocked.Increment(ref _nextId), Ops.Ads), ct);
            if (r.Ok && r.Ads is not null)
            {
                var pending = r.Ads.Count(a => string.IsNullOrEmpty(a.Path));
                Apply(Build(r.Ads, _dir, _broken), now);
                _nextFetch = now + (pending > 0 ? RetryWhileDownloading : RefreshEvery);
            }
            else _nextFetch = now + RetryAfterFailure;   // старые баннеры остаются; повторим скоро
        }
        Rotate(now);
    }

    /// <summary>Картинку не удалось показать (повреждена): убираем баннер из очереди, пока сервер не пришлёт новую версию.</summary>
    public void MarkBroken(AdSlide slide)
    {
        _broken.Add((slide.Id, slide.Version));
        var rest = _slides.Where(s => s != slide).ToList();
        Apply(rest, _time.GetUtcNow());
    }

    /// <summary>
    /// Приводит ответ службы к безопасному виду: только скачанные файлы из папки баннеров, png/jpg, не больше MaxSlides,
    /// время показа в разумных пределах, подпись в одну строку.
    /// </summary>
    public static IReadOnlyList<AdSlide> Build(IEnumerable<AdInfo> ads, string adsDir, ISet<(int, string)>? broken = null)
    {
        var list = new List<AdSlide>();
        foreach (var ad in ads)
        {
            if (list.Count >= MaxSlides) break;
            if (broken?.Contains((ad.Id, ad.Version)) == true) continue;
            if (ImageFiles.Safe(ad.Path, adsDir) is not { } full) continue;
            list.Add(new AdSlide(ad.Id, ad.Version, full, Meta(ad), Math.Clamp(ad.Seconds, MinSeconds, MaxSeconds), ad.Width, ad.Height));
        }
        return list;
    }

    private void Apply(IReadOnlyList<AdSlide> slides, DateTimeOffset now)
    {
        if (!slides.SequenceEqual(_slides)) { _slides = slides; OnChanged(nameof(Slides)); }
        if (slides.Count == 0) { Current = null; return; }
        var same = _current is null ? null : slides.FirstOrDefault(s => s.Id == _current.Id && s.Version == _current.Version);
        if (same is not null) { Current = same; return; }   // тот же баннер остаётся на экране, его время не сбрасываем
        Show(slides[0], now);
    }

    private void Rotate(DateTimeOffset now)
    {
        if (_slides.Count < 2 || _current is null || now < _until) return;
        var index = _slides.ToList().FindIndex(s => s.Id == _current.Id && s.Version == _current.Version);
        Show(_slides[(index + 1) % _slides.Count], now);
    }

    private void Show(AdSlide slide, DateTimeOffset now)
    {
        _until = now + TimeSpan.FromSeconds(slide.Seconds);
        Current = slide;
    }

    private static string Meta(AdInfo a)
    {
        var parts = new List<string>();
        var advertiser = OneLine(a.Advertiser, 120);
        var erid = OneLine(a.Erid, 80);
        if (advertiser.Length > 0) parts.Add(advertiser);
        if (erid.Length > 0) parts.Add($"erid: {erid}");
        return string.Join("  ", parts);
    }

    private static string OneLine(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var cleaned = string.Join(' ', new string(value.Select(ch => char.IsControl(ch) ? ' ' : ch).ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return cleaned.Length <= max ? cleaned : cleaned[..max];
    }

    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
