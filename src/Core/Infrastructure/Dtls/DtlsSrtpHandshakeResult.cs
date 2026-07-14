using Org.BouncyCastle.Tls;

namespace CalloraVoipSdk.Core.Infrastructure.Dtls;

/// <summary>
/// Outcome of a successful DTLS-SRTP handshake: the exported SRTP keys plus the live
/// DTLS transport. The transport is not used for media (SRTP runs directly over UDP,
/// RFC 5764 §4.2) but must be kept open for the session lifetime and closed on teardown
/// so the peer receives a proper <c>close_notify</c>.
/// </summary>
internal sealed class DtlsSrtpHandshakeResult : IDisposable
{
    private readonly DtlsTransport _transport;
    private int _disposed;

    public DtlsSrtpHandshakeResult(DtlsSrtpNegotiatedKeys keys, DtlsTransport transport)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(transport);
        Keys = keys;
        _transport = transport;
    }

    /// <summary>SRTP master keys for both directions (RFC 5764 §4.2).</summary>
    public DtlsSrtpNegotiatedKeys Keys { get; }

    /// <summary>
    /// Closes the DTLS association (sends <c>close_notify</c> via the underlying datagram
    /// transport). Idempotent and safe to call concurrently.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _transport.Close();
    }
}
