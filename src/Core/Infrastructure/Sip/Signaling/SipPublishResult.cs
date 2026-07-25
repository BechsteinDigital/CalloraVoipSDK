namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Outcome of an out-of-dialog SIP PUBLISH (RFC 3903): the final status code plus the entity-tag and
/// granted lifetime the event state compositor returned on a 2xx, which a caller uses to refresh, modify
/// or remove the publication later (SIP-If-Match).
/// </summary>
/// <param name="StatusCode">The final SIP response status code (a 2xx on success).</param>
/// <param name="ETag">The SIP-ETag from a 2xx response, or <see langword="null"/> when absent.</param>
/// <param name="ExpiresSeconds">The granted publication lifetime from a 2xx Expires header, or 0.</param>
internal sealed record SipPublishResult(int StatusCode, string? ETag, int ExpiresSeconds);
