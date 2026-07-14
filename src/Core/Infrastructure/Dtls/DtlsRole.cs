namespace CalloraVoipSdk.Core.Infrastructure.Dtls;

/// <summary>
/// DTLS handshake role for a media stream, negotiated via the SDP <c>a=setup</c>
/// attribute (RFC 5763 §5 / RFC 4145): <c>active</c> becomes the DTLS client,
/// <c>passive</c> the DTLS server.
/// </summary>
internal enum DtlsRole
{
    /// <summary>This endpoint initiates the DTLS handshake (SDP <c>a=setup:active</c>).</summary>
    Client,

    /// <summary>This endpoint awaits the DTLS handshake (SDP <c>a=setup:passive</c>).</summary>
    Server,
}
