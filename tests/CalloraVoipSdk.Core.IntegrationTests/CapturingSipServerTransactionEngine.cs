using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transactions.Server;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// A server transaction engine that records the responses sent through it, so a test can assert what status a
/// session's inbound handling produced (e.g. a 481 for a dialog-identity mismatch, CF-013).
/// </summary>
internal sealed class CapturingSipServerTransactionEngine : ISipServerTransactionEngine
{
    private readonly List<(int StatusCode, string ReasonPhrase)> _responses = new();

    public IReadOnlyList<(int StatusCode, string ReasonPhrase)> Responses => _responses;

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
        CancellationToken ct = default)
    {
        _responses.Add((statusCode, reasonPhrase));
        return Task.CompletedTask;
    }

    public void RegisterTransportErrorHandler(Action<SipServerTransactionKey, Exception> handler)
    {
    }
}
