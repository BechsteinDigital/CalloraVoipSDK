using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transactions.Server;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

internal sealed class NoopSipServerTransactionEngine : ISipServerTransactionEngine
{
    public void Dispose()
    {
    }

    public SipServerTransactionRegistration RegisterInboundRequest(
        IPEndPoint remoteEndPoint,
        SipTransportProtocol transport,
        SipRequest request) =>
        new();

    public Task SendResponseAsync(
        SipRequest request,
        IPEndPoint remoteEndPoint,
        SipTransportProtocol transport,
        int statusCode,
        string reasonPhrase,
        IReadOnlyDictionary<string, string> headers,
        string? body,
        CancellationToken ct = default) =>
        Task.CompletedTask;

    public void RegisterTransportErrorHandler(Action<SipServerTransactionKey, Exception> handler)
    {
    }
}
