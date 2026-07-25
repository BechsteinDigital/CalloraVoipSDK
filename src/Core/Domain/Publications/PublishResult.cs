namespace CalloraVoipSdk.Core.Domain.Publications;

/// <summary>
/// Outcome of a successful SIP PUBLISH (RFC 3903 event state publication). Carries the entity-tag and
/// granted lifetime the event state compositor returned, which identify the publication for a later
/// refresh, modify or remove (SIP-If-Match).
/// </summary>
/// <param name="ETag">The SIP-ETag the compositor assigned, or <see langword="null"/> when it sent none.</param>
/// <param name="ExpiresSeconds">The granted publication lifetime in seconds (0 when the response omitted it).</param>
public sealed record PublishResult(string? ETag, int ExpiresSeconds);
