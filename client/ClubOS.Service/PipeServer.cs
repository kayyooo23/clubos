using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using ClubOS.Shared;

namespace ClubOS.Service;

/// <summary>
/// Локальный канал, через который оверлей разговаривает со службой. Доступ: вошедшие пользователи этого компьютера
/// (гость запускает оверлей от своего имени) и система; из сети — никто.
/// </summary>
public sealed class PipeServer(AgentCore core, string pipeName, ILogger<PipeServer> log) : BackgroundService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim _slots = new(16); // не больше 16 запросов одновременно

    public static PipeSecurity BuildSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        var me = WindowsIdentity.GetCurrent().User; // тот, кто запустил службу, должен уметь создавать новые экземпляры канала
        if (me is not null) security.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private NamedPipeServerStream CreateServer() => NamedPipeServerStreamAcl.Create(
        pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous, 0, 0, BuildSecurity());

    /// <summary>Сколько слушателей ждут клиентов одновременно. Один слушатель оставлял бы «окно» между приёмом
    /// соединения и созданием следующего экземпляра канала, и клиент в этот момент не мог бы подключиться.</summary>
    private const int Listeners = 4;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("Канал для оверлея: {Pipe}", pipeName);
        await Task.WhenAll(Enumerable.Range(0, Listeners).Select(_ => ListenAsync(stoppingToken)));
    }

    private async Task ListenAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = CreateServer();
                await server.WaitForConnectionAsync(stoppingToken);
                await _slots.WaitAsync(stoppingToken);
                var connected = server;
                server = null; // дальше им владеет обработчик
                _ = Task.Run(() => HandleAsync(connected, stoppingToken), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                log.LogError(e, "Сбой канала, пробуем снова");
                try { await Task.Delay(500, stoppingToken); } catch (OperationCanceledException) { break; }
            }
            finally { server?.Dispose(); }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken stopping)
    {
        try
        {
            await using (pipe)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                cts.CancelAfter(RequestTimeout);
                IpcResponse response;
                try
                {
                    var line = await PipeLine.ReadLineAsync(pipe, cts.Token);
                    if (line is null) return;
                    var request = JsonSerializer.Deserialize<IpcRequest>(line, Json.Options);
                    response = request is null
                        ? IpcResponse.Fail(0, 400, "Пустой запрос", core.Snapshot())
                        : await core.HandleAsync(request, cts.Token);
                }
                catch (Exception e) when (e is JsonException or InvalidDataException)
                {
                    response = IpcResponse.Fail(0, 400, "Некорректный запрос", core.Snapshot());
                }
                await PipeLine.WriteAsync(pipe, response, cts.Token);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // оверлей закрылся на полуслове или запрос завис — это не повод шуметь в журнале
        }
        catch (Exception e) { log.LogError(e, "Ошибка обработки запроса"); }
        finally { _slots.Release(); }
    }
}
