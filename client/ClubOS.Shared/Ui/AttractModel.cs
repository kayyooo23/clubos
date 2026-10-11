using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ClubOS.Shared.Ui;

public enum InputKind { Move, Click, Key }

/// <summary>
/// Заставка с рекламой. Пока за ПК никого нет, на весь экран идёт баннер (если клуб включил рекламу и баннеры уже скачаны). Гость садится, нажимает
/// кнопку мыши или клавишу (или заметно двигает мышь), и вместо заставки появляется форма входа. Если он ушёл и ничего не трогает, заставка возвращается.
/// Не знает про WPF: окно подсказывает, было ли нажатие, и показывает заставку, пока IsAttract = true.
///
/// Правила:
///   • заставка нужна, только когда баннеры есть, гость не вошёл и сессии нет (в кабинете и в игре её не бывает);
///   • ПК только что освободился (ушёл гость, кончилась сессия, ПК загрузился): заставка сразу, если последние секунды никто ничего не трогал;
///   • пока гость вводит данные, заставка не вылезает; на форме входа с пустыми полями она вернётся через 45 секунд тишины, а если что-то введено
///     (регистрация, код из SMS), через 3 минуты, причём введённое стирается: следующий гость его не увидит;
///   • нажатие, разбудившее заставку, дальше не идёт (иначе клик попал бы в поле формы под баннером);
///   • оверлей сообщает службе, на экране ли заставка (раз в 5 секунд и при каждой смене): по этому сервер считает время показа.
/// </summary>
public sealed class AttractModel : INotifyPropertyChanged
{
    public static readonly TimeSpan LoginIdle = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan LongFormIdle = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan QuietBeforeStart = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan ReportEvery = TimeSpan.FromSeconds(5);
    public const double MoveDistance = 30;   // пикселей: дрожь мыши на столе будить экран не должна

    private readonly FlowController _flow;
    private readonly IAgentClient _client;
    private readonly TimeProvider _time;
    private int _nextId;
    private bool _isAttract, _wasWanted, _reported;
    private bool _lastReportedValue;
    private DateTimeOffset _lastInput = DateTimeOffset.MinValue, _lastReport = DateTimeOffset.MinValue;
    private (double X, double Y)? _anchor;

    public AttractModel(FlowController flow, AdBannerModel ads, IAgentClient client, TimeProvider? time = null)
    {
        _flow = flow;
        Ads = ads;
        _client = client;
        _time = time ?? TimeProvider.System;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Баннеры заставки (список, смена по кругу).</summary>
    public AdBannerModel Ads { get; }

    /// <summary>Заставка сейчас на экране.</summary>
    public bool IsAttract { get => _isAttract; private set { if (_isAttract != value) { _isAttract = value; OnChanged(); OnChanged(nameof(ShowForm)); } } }

    /// <summary>Форма входа видна (заставки нет). Экран «кабинет» это не затрагивает: он показывается отдельно.</summary>
    public bool ShowForm => !_isAttract;

    private bool Wanted => Ads.HasAds && _flow.Screen != Screen.Cabinet && !_flow.HasSession;

    /// <summary>
    /// Окно сообщает о вводе. Возвращает true, если этот ввод разбудил заставку: окно не должно пускать его дальше (клик в форму, символ в поле).
    /// Движение мыши будит и обновляет «последний ввод» только когда ушло дальше MoveDistance от места, где она лежала.
    /// </summary>
    public bool Input(InputKind kind, double x = 0, double y = 0)
    {
        if (kind == InputKind.Move)
        {
            if (_anchor is not { } anchor) { _anchor = (x, y); return false; }
            if (Math.Sqrt(Math.Pow(x - anchor.X, 2) + Math.Pow(y - anchor.Y, 2)) < MoveDistance) return false;
            _anchor = (x, y);
        }
        _lastInput = _time.GetUtcNow();
        if (!IsAttract) return false;
        IsAttract = false;
        _anchor = null;
        // движение мыши будит экран, но само по себе ничего не «съедает»: клика или клавиши ещё не было
        return kind != InputKind.Move;
    }

    /// <summary>Раз в секунду: решает, пора ли включать заставку, и сообщает службе, видна ли она.</summary>
    public async Task PollAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        Reevaluate(now);
        if (!_reported || _lastReportedValue != IsAttract || now - _lastReport >= ReportEvery)
        {
            _reported = true;
            _lastReportedValue = IsAttract;
            _lastReport = now;
            await _client.SendAsync(new IpcRequest(Interlocked.Increment(ref _nextId), Ops.AdsVisible, Text: IsAttract ? "1" : "0"), ct);
        }
    }

    private void Reevaluate(DateTimeOffset now)
    {
        if (!Wanted)
        {
            _wasWanted = false;
            IsAttract = false;
            return;
        }
        if (!_wasWanted)
        {
            // заставка стала нужна (баннеры появились, гость вышел, сессия кончилась): сразу, если рядом никто ничего не делал
            _wasWanted = true;
            if (now - _lastInput >= QuietBeforeStart) Start();
            return;
        }
        if (IsAttract || _flow.Busy) return;

        var formIsEmpty = _flow.Screen == Screen.Login && _flow.Phone.Length == 0 && _flow.Password.Length == 0;
        if (now - _lastInput < (formIsEmpty ? LoginIdle : LongFormIdle)) return;
        if (!formIsEmpty)
        {
            _flow.GoTo(Screen.Login);   // стирает пароль, код и ошибки
            _flow.Phone = "";
        }
        Start();
    }

    private void Start()
    {
        _anchor = null;
        IsAttract = true;
    }

    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
