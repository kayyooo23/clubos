using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ClubOS.Shared;

/// <summary>
/// Операции, которые оверлей может просить у службы. Это ВСЁ, что ему доступно: ни ключа ПК, ни токена гостя
/// он не видит, произвольных запросов к серверу отправить не может.
/// </summary>
public static class Ops
{
    public const string State = "state";
    public const string Tariffs = "tariffs";
    public const string Apps = "apps";
    public const string Products = "products";
    public const string Ads = "ads";
    public const string AdsVisible = "ads.visible";   // оверлей сообщает: заставка с баннером сейчас на экране (Text = "1") или нет ("0")
    public const string OrderCreate = "order.create";
    public const string Orders = "orders";
    public const string OrderCancel = "order.cancel";
    public const string CallAdmin = "call";
    public const string CallState = "call.state";
    public const string RegisterRequestCode = "register.request_code";
    public const string RegisterConfirm = "register.confirm";
    public const string ResetRequestCode = "reset.request_code";
    public const string ResetConfirm = "reset.confirm";
    public const string Login = "login";
    public const string Logout = "logout";
    public const string KeepAlive = "keepalive";
    public const string Transactions = "transactions";
    public const string StartSession = "session.start";
    public const string StopSession = "session.stop";
    public const string SessionExtend = "session.extend";   // продлить идущую сессию пакетом часов (TariffId)

    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        State, Tariffs, Apps, Products, Ads, AdsVisible, OrderCreate, Orders, OrderCancel, CallAdmin, CallState, RegisterRequestCode, RegisterConfirm, ResetRequestCode, ResetConfirm,
        Login, Logout, KeepAlive, Transactions, StartSession, StopSession, SessionExtend,
    };
}

/// <summary>Запрос оверлея к службе. Поля заполняются по операции; остальные пустые.</summary>
public sealed record IpcRequest(
    int Id, string Op, string? Phone = null, string? Password = null, string? Code = null,
    bool AcceptTerms = false, int TariffId = 0, IReadOnlyList<CartLine>? Items = null, string? Text = null, int OrderId = 0);

/// <summary>Ответ службы. Snapshot всегда свежий, чтобы экран обновлялся сразу после действия.</summary>
public sealed record IpcResponse(
    int Id, bool Ok, int Status, string? Error, AgentSnapshot? Snapshot,
    IReadOnlyList<TariffInfo>? Tariffs = null, IReadOnlyList<TransactionInfo>? Transactions = null,
    int? ResendAfter = null, IReadOnlyList<AppInfo>? Apps = null, IReadOnlyList<ProductInfo>? Products = null,
    IReadOnlyList<OrderInfo>? Orders = null, CallInfo? OpenCall = null, IReadOnlyList<AdInfo>? Ads = null)
{
    public static IpcResponse Fail(int id, int status, string error, AgentSnapshot? snapshot = null)
        => new(id, false, status, error, snapshot);
}

/// <summary>Как оверлей отправляет запросы службе (в тестах подменяется заглушкой).</summary>
public interface IAgentClient
{
    Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken ct = default);
}

/// <summary>Один запрос = одна строка JSON. Размер ограничен, чтобы мусор в канале не раздувал память службы.</summary>
public static class PipeLine
{
    public const int MaxBytes = 64 * 1024;

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Json.Options) + "\n");
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Читает одну строку. Возвращает null, если собеседник закрыл канал; бросает InvalidDataException при превышении размера.</summary>
    public static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var buf = new List<byte>(256);
        var one = new byte[1];
        while (true)
        {
            var n = await stream.ReadAsync(one, ct).ConfigureAwait(false);
            if (n == 0) return buf.Count == 0 ? null : Encoding.UTF8.GetString(buf.ToArray());
            if (one[0] == (byte)'\n') return Encoding.UTF8.GetString(buf.ToArray());
            if (buf.Count >= MaxBytes) throw new InvalidDataException("Сообщение слишком большое");
            buf.Add(one[0]);
        }
    }
}

/// <summary>Клиент канала для оверлея: на каждый запрос своё короткое соединение со службой.</summary>
public sealed class PipeAgentClient(string pipeName, TimeSpan? timeout = null, int connectMilliseconds = 2000) : IAgentClient
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(15);

    public async Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(connectMilliseconds, cts.Token).ConfigureAwait(false);
            await PipeLine.WriteAsync(pipe, request, cts.Token).ConfigureAwait(false);
            var line = await PipeLine.ReadLineAsync(pipe, cts.Token).ConfigureAwait(false);
            if (line is null) return IpcResponse.Fail(request.Id, 0, "Служба ClubOS не ответила");
            return JsonSerializer.Deserialize<IpcResponse>(line, Json.Options)
                   ?? IpcResponse.Fail(request.Id, 0, "Служба ClubOS вернула пустой ответ");
        }
        catch (Exception e) when (e is TimeoutException or IOException or OperationCanceledException or InvalidDataException or JsonException)
        {
            if (ct.IsCancellationRequested) throw;
            return IpcResponse.Fail(request.Id, 0, "Служба ClubOS недоступна");
        }
    }
}
