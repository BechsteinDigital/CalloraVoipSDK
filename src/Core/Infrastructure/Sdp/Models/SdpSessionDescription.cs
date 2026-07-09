namespace CalloraVoipSdk.Core.Infrastructure.Sdp.Models;

/// <summary>
/// Parsed / constructed SDP session model (RFC 4566, updated by RFC 8866).
/// Carries session-level ICE credentials (RFC 8839) and BUNDLE group (RFC 5888).
/// </summary>
internal sealed class SdpSessionDescription
{
    /// <summary>Session origin host address (<c>o=</c> address field).</summary>
    public required string OriginAddress { get; init; }

    /// <summary>Origin session id (<c>o=</c> sess-id, RFC 4566 §5.2). Stable per session.</summary>
    public long SessionId { get; init; }

    /// <summary>
    /// Origin session version (<c>o=</c> sess-version, RFC 4566 §5.2). Incremented whenever the
    /// session description is modified so the peer detects the change (RFC 3264 §5).
    /// </summary>
    public long SessionVersion { get; init; }

    /// <summary>Session-level connection host address (<c>c=</c>).</summary>
    public required string ConnectionAddress { get; init; }

    /// <summary>Session-level media direction fallback.</summary>
    public SdpMediaDirection SessionDirection { get; init; } = SdpMediaDirection.SendRecv;

    /// <summary>Media sections in declaration order.</summary>
    public required IReadOnlyList<SdpMediaDescription> Media { get; init; }

    // -------------------------------------------------------------------------
    // BUNDLE grouping (RFC 5888)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Raw <c>a=group</c> value, e.g. <c>BUNDLE audio video</c> (RFC 5888 §5).
    /// <see langword="null"/> when no group attribute is present.
    /// </summary>
    public string? Group { get; init; }

    // -------------------------------------------------------------------------
    // Session-level ICE credentials (RFC 8839 §5.4)
    // -------------------------------------------------------------------------

    /// <summary>Session-level ICE username fragment (<c>a=ice-ufrag</c>).</summary>
    public string? IceUfrag { get; init; }

    /// <summary>Session-level ICE password (<c>a=ice-pwd</c>).</summary>
    public string? IcePwd { get; init; }

    /// <summary>Session-level ICE options string (<c>a=ice-options</c>).</summary>
    public string? IceOptions { get; init; }

    // -------------------------------------------------------------------------
    // DTLS-SRTP (RFC 5763 / RFC 8122 / RFC 4145)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Session-level DTLS certificate fingerprint (<c>a=fingerprint</c>, RFC 8122).
    /// Media-level fingerprint takes precedence when present.
    /// </summary>
    public SdpFingerprint? Fingerprint { get; init; }

    /// <summary>
    /// Session-level DTLS setup role (<c>a=setup</c>, RFC 4145).
    /// Media-level setup takes precedence when present.
    /// </summary>
    public string? DtlsSetup { get; init; }
}
