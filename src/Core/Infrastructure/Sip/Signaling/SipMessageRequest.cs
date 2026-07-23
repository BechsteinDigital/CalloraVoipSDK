using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Input model for sending an out-of-dialog SIP MESSAGE (RFC 3428 pager-mode instant message).
/// </summary>
internal sealed record SipMessageRequest
{
    /// <summary>SIP username used in the local From URI (e.g. "alice").</summary>
    public string LocalUsername { get; init; } = string.Empty;

    /// <summary>SIP domain used in the local From URI (e.g. "example.com").</summary>
    public string LocalDomain { get; init; } = string.Empty;

    /// <summary>The recipient's SIP URI (Request-URI and To header).</summary>
    public string RemoteUri { get; init; } = string.Empty;

    /// <summary>The message body to send (for example the IM text).</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>The MIME content type of <see cref="Body"/> (RFC 3428 default <c>text/plain</c>).</summary>
    public string ContentType { get; init; } = "text/plain";

    /// <summary>The clear-text password used to answer a 401/407 digest challenge; null skips auth.</summary>
    public string? AuthPassword { get; init; }

    /// <summary>Transport used to reach the recipient.</summary>
    public SipTransportProtocol Transport { get; init; } = SipTransportProtocol.Udp;

    /// <summary>Per-attempt transaction timeout.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(32);
}
