using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Common immutable configuration values for creating one SIP dialog session.
/// </summary>
internal sealed class SipCallSessionConfiguration
{
    /// <summary>
    /// SIP Call-ID value for the dialog.
    /// </summary>
    public required string CallId { get; init; }

    /// <summary>
    /// Local SIP URI used for From/Contact headers.
    /// </summary>
    public required string LocalUri { get; init; }

    /// <summary>
    /// Remote SIP URI used as request target and To header.
    /// </summary>
    public required string RemoteUri { get; init; }

    /// <summary>
    /// Optional initial Request-URI override for first out-of-dialog request.
    /// </summary>
    public string? InitialRequestUri { get; init; }

    /// <summary>
    /// Optional initial Route header set used before dialog route-set is established.
    /// </summary>
    public IReadOnlyList<string> InitialRouteSet { get; init; } = [];

    /// <summary>
    /// Optional local display name used in From header formatting.
    /// </summary>
    public string? LocalDisplayName { get; init; }

    /// <summary>
    /// Optional preferred identity URI used for outbound <c>P-Preferred-Identity</c> header emission.
    /// </summary>
    public string? PreferredIdentityUri { get; init; }

    /// <summary>
    /// Optional <c>Privacy</c> header value applied to outbound INVITE requests (RFC 3323).
    /// </summary>
    public string? PrivacyHeader { get; init; }

    /// <summary>
    /// Optional Require header value for outbound requests.
    /// </summary>
    public string? RequireHeader { get; init; }

    /// <summary>
    /// Optional Proxy-Require header value for outbound requests.
    /// </summary>
    public string? ProxyRequireHeader { get; init; }

    /// <summary>
    /// Optional <c>Referred-By</c> header value (RFC 3892) included in the initial INVITE
    /// when this dialog is placed as a result of a received REFER transfer.
    /// </summary>
    public string? ReferredBy { get; init; }

    /// <summary>
    /// Authentication username used for SIP digest challenges.
    /// </summary>
    public required string AuthUsername { get; init; }

    /// <summary>
    /// Optional authentication password used for SIP digest challenges.
    /// </summary>
    public string? AuthPassword { get; init; }

    /// <summary>
    /// User-Agent header value sent by this dialog.
    /// </summary>
    public required string UserAgent { get; init; }

    /// <summary>
    /// Transaction timeout for dialog requests and response waits.
    /// </summary>
    public required TimeSpan Timeout { get; init; }

    /// <summary>
    /// Current remote transport endpoint used for request dispatch.
    /// </summary>
    public required IPEndPoint RemoteEndPoint { get; init; }

    /// <summary>
    /// SIP transport protocol used for dialog signaling.
    /// </summary>
    public SipTransportProtocol SignalingTransport { get; init; } = SipTransportProtocol.Udp;

    /// <summary>
    /// Optional extra SIP headers applied to the outbound initial INVITE. Protected headers are
    /// ignored by the header builder. <see langword="null"/> for inbound dialogs.
    /// </summary>
    public IReadOnlyDictionary<string, string>? CustomHeaders { get; init; }
}
