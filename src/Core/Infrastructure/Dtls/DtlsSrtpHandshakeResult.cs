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
    /// The live DTLS transport, exposed so the association can be serviced after key export (#190):
    /// a control-receive loop polls <see cref="DtlsTransport.Receive(byte[], int, int, int)"/> to
    /// notice a peer <c>close_notify</c> or alert and to discard stray DTLS application_data. Media
    /// itself never flows here — SRTP runs directly over UDP (RFC 5764 §4.2).
    /// </summary>
    internal DtlsTransport Transport => _transport;

    /// <summary>
    /// Closes the DTLS association (sends <c>close_notify</c> via the underlying datagram
    /// transport) and wipes the exported SRTP master key material. Idempotent and safe to call
    /// concurrently.
    /// </summary>
    /// <remarks>
    /// #157 P2-6: disposing the result must wipe the keys, not just close the transport. A result that
    /// is discarded rather than consumed — the caller cancelling in the instant the handshake completed
    /// — otherwise leaves the exported master halves on the managed heap with nobody left to wipe them
    /// (RFC 3711 §9.4). Wiping here is safe on the normal path too: SRTP/SRTCP contexts hold their own
    /// derived session keys, and <see cref="DtlsSrtpNegotiatedKeys.Dispose"/> is idempotent, so the
    /// consuming path's earlier wipe is simply repeated.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Keys.Dispose();
        _transport.Close();
    }
}
