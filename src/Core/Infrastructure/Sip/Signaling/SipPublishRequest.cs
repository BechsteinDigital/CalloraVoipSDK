using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Input model for sending an out-of-dialog SIP PUBLISH (RFC 3903 event state publication).
/// </summary>
internal sealed record SipPublishRequest
{
    /// <summary>SIP username used in the local From URI (e.g. "alice").</summary>
    public string LocalUsername { get; init; } = string.Empty;

    /// <summary>SIP domain used in the local From URI (e.g. "example.com").</summary>
    public string LocalDomain { get; init; } = string.Empty;

    /// <summary>The event-state resource URI (Request-URI and To header).</summary>
    public string RemoteUri { get; init; } = string.Empty;

    /// <summary>The event package being published (Event header, e.g. "presence").</summary>
    public string EventType { get; init; } = string.Empty;

    /// <summary>The event-state document to publish (for example a PIDF body).</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>The MIME content type of <see cref="Body"/> (e.g. <c>application/pidf+xml</c>).</summary>
    public string ContentType { get; init; } = "text/plain";

    /// <summary>Requested publication lifetime in seconds (Expires header, RFC 3903 §4).</summary>
    public int ExpiresSeconds { get; init; } = 3600;

    /// <summary>The clear-text password used to answer a 401/407 digest challenge; null skips auth.</summary>
    public string? AuthPassword { get; init; }

    /// <summary>Transport used to reach the event state compositor.</summary>
    public SipTransportProtocol Transport { get; init; } = SipTransportProtocol.Udp;

    /// <summary>Per-attempt transaction timeout.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(32);
}
