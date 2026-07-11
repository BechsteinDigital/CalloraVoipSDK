namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Input model for outbound SIP INVITE operations.
/// </summary>
internal sealed class SipInviteRequest
{
    /// <summary>
    /// Local user part for From/Contact headers.
    /// </summary>
    public required string LocalUsername { get; init; }

    /// <summary>
    /// Local SIP domain used in From/Contact headers.
    /// </summary>
    public required string LocalDomain { get; init; }

    /// <summary>
    /// Optional display-name used in the From header.
    /// </summary>
    public string? LocalDisplayName { get; init; }

    /// <summary>
    /// Optional preferred identity URI sent as <c>P-Preferred-Identity</c> on outbound INVITE.
    /// This is only a preference hint; trusted network elements may ignore it.
    /// </summary>
    public string? PreferredIdentityUri { get; init; }

    /// <summary>
    /// Optional <c>Privacy</c> header value for outbound INVITE (RFC 3323).
    /// Use <c>"id"</c> to request that the network suppress the caller's identity
    /// toward the remote party. The network may still use <c>P-Preferred-Identity</c>
    /// for billing while honouring the privacy request.
    /// </summary>
    public string? Privacy { get; init; }

    /// <summary>
    /// Optional Require header value for extensions that must be understood by UAS.
    /// </summary>
    public string? RequireHeader { get; init; }

    /// <summary>
    /// Optional Proxy-Require header value for extensions that must be understood by proxies.
    /// </summary>
    public string? ProxyRequireHeader { get; init; }

    /// <summary>
    /// Optional <c>Referred-By</c> header value (RFC 3892) identifying the transferor
    /// when this INVITE is placed as a result of a received REFER.
    /// Value must be a SIP URI in addr-spec or name-addr format (e.g. <c>sip:alice@example.org</c>).
    /// </summary>
    public string? ReferredBy { get; init; }

    /// <summary>
    /// Optional preloaded Route-set for out-of-dialog routing policy.
    /// Values may be plain SIP/SIPS URIs or name-addr values.
    /// </summary>
    public IReadOnlyList<string> PreloadedRouteSet { get; init; } = [];

    /// <summary>
    /// Optional SIP auth username for INVITE digest challenge.
    /// If null, LocalUsername is used.
    /// </summary>
    public string? AuthUsername { get; init; }

    /// <summary>
    /// Optional SIP auth password for INVITE digest challenge.
    /// </summary>
    public string? AuthPassword { get; init; }

    /// <summary>
    /// Remote target SIP URI (for example sip:bob@example.com).
    /// </summary>
    public required string RemoteUri { get; init; }

    /// <summary>
    /// Remote SIP port.
    /// </summary>
    public int RemotePort { get; init; } = 5060;

    /// <summary>
    /// SDP offer body for INVITE.
    /// If null, a minimal default SDP offer is generated.
    /// </summary>
    public string? SessionDescription { get; init; }

    /// <summary>
    /// Transaction timeout for INVITE/re-INVITE/BYE operations.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// User-Agent header value.
    /// </summary>
    public string UserAgent { get; init; } = "CalloraVoipSdk/1.0";

    /// <summary>
    /// Preferred SIP signaling transport for this INVITE dialog.
    /// </summary>
    public Infrastructure.Sip.Transport.SipTransportProtocol Transport { get; init; } =
        Infrastructure.Sip.Transport.SipTransportProtocol.Udp;

    /// <summary>
    /// Optional extra SIP headers to add to the outbound INVITE. Protected dialog/transport headers
    /// (Via, From, To, Call-ID, CSeq, Contact, Content-*, Route, authorization, ...) cannot be
    /// overridden and are ignored if present here.
    /// </summary>
    public IReadOnlyDictionary<string, string>? CustomHeaders { get; init; }
}
