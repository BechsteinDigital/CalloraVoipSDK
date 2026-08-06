namespace CalloraVoipSdk.Core.Infrastructure.Dtls;

/// <summary>
/// Outcome of servicing the post-handshake DTLS keying channel once (#190). Normalises the mixed
/// BouncyCastle post-handshake signalling (a plain -1 for a timeout, a positive length for
/// application_data, and a thrown <c>TlsFatalAlert</c> / closed transport for a peer close) into a
/// single discriminated result the receive loop can act on.
/// </summary>
internal enum DtlsControlSignal
{
    /// <summary>Nothing arrived this interval — keep polling.</summary>
    Timeout,

    /// <summary>DTLS application_data arrived; in pure-SRTP mode it is discarded (RFC 5764).</summary>
    ApplicationData,

    /// <summary>The association is closed — a peer close_notify/alert, or our own teardown.</summary>
    Closed,
}

/// <summary>One control-receive outcome and, for <see cref="DtlsControlSignal.ApplicationData"/>, its length.</summary>
internal readonly record struct DtlsControlReceiveResult(DtlsControlSignal Signal, int Length);

/// <summary>
/// The post-handshake DTLS control channel the association receive loop polls (#190). Abstracts the
/// BouncyCastle <c>DtlsTransport</c> so the loop is testable without a live handshake; the production
/// adapter maps BouncyCastle's post-handshake semantics onto <see cref="DtlsControlReceiveResult"/>.
/// </summary>
internal interface IDtlsControlChannel
{
    /// <summary>
    /// Receives one control record, waiting up to <paramref name="waitMillis"/>. Never blocks longer
    /// than that, so the loop stays responsive to cancellation. May throw on an unexpected fault; the
    /// loop treats that fail-closed.
    /// </summary>
    DtlsControlReceiveResult Receive(Span<byte> buffer, int waitMillis);
}
