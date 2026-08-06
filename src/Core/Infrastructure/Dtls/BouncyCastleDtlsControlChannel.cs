using Org.BouncyCastle.Tls;

namespace CalloraVoipSdk.Core.Infrastructure.Dtls;

/// <summary>
/// Maps BouncyCastle's post-handshake <see cref="DtlsTransport"/> semantics onto
/// <see cref="DtlsControlReceiveResult"/> for the association receive loop (#190). The semantics are
/// empirically pinned (see DtlsAssociationServicingTests probes):
/// <list type="bullet">
/// <item>a timeout returns -1 with the underlying transport still open;</item>
/// <item>application_data returns a positive length;</item>
/// <item>a peer <c>close_notify</c> makes BouncyCastle close the underlying transport and then, reading
/// on, surface our closed <see cref="QueueDatagramTransport"/> as <c>TlsFatalAlert(internal_error)</c>;</item>
/// <item>a peer fatal alert surfaces as <see cref="TlsFatalAlertReceived"/>.</item>
/// </list>
/// A close is disambiguated from a timeout via <see cref="QueueDatagramTransport.IsClosed"/>.
/// </summary>
internal sealed class BouncyCastleDtlsControlChannel : IDtlsControlChannel
{
    private readonly DtlsTransport _transport;
    private readonly QueueDatagramTransport _underlying;

    public BouncyCastleDtlsControlChannel(DtlsTransport transport, QueueDatagramTransport underlying)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(underlying);
        _transport = transport;
        _underlying = underlying;
    }

    /// <inheritdoc />
    public DtlsControlReceiveResult Receive(Span<byte> buffer, int waitMillis)
    {
        try
        {
            var received = _transport.Receive(buffer, waitMillis);
            if (received > 0)
                return new DtlsControlReceiveResult(DtlsControlSignal.ApplicationData, received);

            // received <= 0: BouncyCastle returned -1 — either a plain receive timeout, or it just
            // processed a peer close_notify and closed the underlying transport.
            return _underlying.IsClosed
                ? new DtlsControlReceiveResult(DtlsControlSignal.Closed, 0)
                : new DtlsControlReceiveResult(DtlsControlSignal.Timeout, 0);
        }
        catch (TlsFatalAlertReceived)
        {
            // The peer sent a fatal alert — the association is terminated by the peer.
            return new DtlsControlReceiveResult(DtlsControlSignal.Closed, 0);
        }
        catch (TlsFatalAlert) when (_underlying.IsClosed)
        {
            // BouncyCastle closed our transport (peer close_notify processed, or our own teardown) and
            // then surfaced the now-closed transport as internal_error while reading on.
            return new DtlsControlReceiveResult(DtlsControlSignal.Closed, 0);
        }
        catch (ObjectDisposedException)
        {
            // The underlying transport was closed under us (teardown) — the association is done.
            return new DtlsControlReceiveResult(DtlsControlSignal.Closed, 0);
        }
        // A genuine local fault (a TlsFatalAlert with the transport still open, or any other
        // exception) is deliberately NOT caught here: it propagates to the receive loop, which
        // treats it fail-closed (ceases media) rather than assuming the channel is healthy (K1).
    }
}
