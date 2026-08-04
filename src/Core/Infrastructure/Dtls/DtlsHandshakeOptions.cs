namespace CalloraVoipSdk.Core.Infrastructure.Dtls;

/// <summary>
/// Tunables for the DTLS-SRTP handshake engine (RFC 5763/5764). Bounds how long a single
/// handshake may run before it is aborted fail-closed, so a silent or stalling peer can never
/// pin a worker thread or the shared media socket open indefinitely (#163 P1-1).
/// </summary>
internal sealed record DtlsHandshakeOptions
{
    /// <summary>
    /// Wall-clock ceiling for one handshake attempt. When it elapses the transport is closed
    /// and the handshake fails with <see cref="DtlsSrtpHandshakeTimeoutException"/>. Default
    /// 20 s mirrors SIPSorcery's DTLS handshake budget: comfortably above a worst-case real
    /// handshake, yet short enough to reclaim a dead leg. Must be positive.
    /// </summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Shared default instance used when no options are supplied.</summary>
    public static DtlsHandshakeOptions Default { get; } = new();
}
