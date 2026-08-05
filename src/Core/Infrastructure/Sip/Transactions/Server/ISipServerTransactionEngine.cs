using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Transactions.Server;

/// <summary>
/// Tracks SIP server transactions and applies response retransmission/timer behavior.
/// </summary>
internal interface ISipServerTransactionEngine : IDisposable
{
    /// <summary>
    /// Registers one inbound request and returns processing guidance. The <see cref="SipInboundRequestContext"/>
    /// carries the real receive transport and accepted inbound connection id, which the transaction retains so
    /// every response — initial, retransmit, or resend — is routed back over that connection (#158 P1-2).
    /// </summary>
    SipServerTransactionRegistration RegisterInboundRequest(
        SipInboundRequestContext context,
        SipRequest request);

    /// <summary>
    /// Sends one response through the server transaction state machine.
    /// </summary>
    Task SendResponseAsync(
        SipRequest request,
        IPEndPoint remoteEndPoint,
        SipTransportProtocol transport,
        int statusCode,
        string reasonPhrase,
        IReadOnlyDictionary<string, string> headers,
        string? body,
        CancellationToken ct = default);

    /// <summary>
    /// Registers a callback invoked when a fatal transport error terminates a server transaction
    /// (RFC 3261 §17.2.4). The callback receives the transaction key and the transport exception.
    /// Only one handler can be registered; subsequent calls replace the previous one.
    /// </summary>
    void RegisterTransportErrorHandler(Action<SipServerTransactionKey, Exception> handler);
}

