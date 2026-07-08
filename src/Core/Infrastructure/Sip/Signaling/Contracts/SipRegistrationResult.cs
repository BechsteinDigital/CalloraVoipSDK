namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Result model for SIP REGISTER operations.
/// </summary>
internal sealed class SipRegistrationResult
{
    /// <summary>
    /// SIP Call-ID used for this registration transaction.
    /// Pass this back via <see cref="SipRegistrationRequest.ExistingCallId"/> when refreshing
    /// the same binding (RFC 3261 §10.2.4).
    /// </summary>
    public required string CallId { get; init; }

    /// <summary>
    /// Final SIP response status code.
    /// </summary>
    public required int StatusCode { get; init; }

    /// <summary>
    /// Effective registration lifetime in seconds granted by the registrar.
    /// </summary>
    public required int EffectiveExpiresSeconds { get; init; }

    /// <summary>
    /// Contact URI sent in the REGISTER request.
    /// </summary>
    public required string ContactUri { get; init; }

    /// <summary>
    /// True if digest authentication challenge/response was used.
    /// </summary>
    public required bool Authenticated { get; init; }

    /// <summary>
    /// CSeq to use for the next REGISTER refresh transaction.
    /// Pass this back via <see cref="SipRegistrationRequest.StartCSeq"/> (RFC 3261 §10.2.4).
    /// </summary>
    public required int NextCSeq { get; init; }

    /// <summary>
    /// RFC 3608 – Service-Route header value returned by the registrar in the 200 OK.
    /// When non-null the UA should use this as the pre-loaded route set for outbound requests.
    /// </summary>
    public string? ServiceRoute { get; init; }

    /// <summary>
    /// Contact bindings returned by the registrar in the 200 OK Contact headers.
    /// Contains one entry per active binding.
    /// </summary>
    public IReadOnlyList<SipRegistrationBinding> RegisteredBindings { get; init; } = [];

    /// <summary>
    /// Public host the registrar observed for this UA, read from the response top Via
    /// <c>received=</c> parameter (RFC 3261 §18.2.1). Behind NAT this is the routable
    /// address to advertise in the Contact. <see langword="null"/> when the registrar did
    /// not reflect it.
    /// </summary>
    public string? ObservedPublicHost { get; init; }

    /// <summary>
    /// Public port the registrar observed for this UA, read from the response top Via
    /// <c>rport=</c> parameter (RFC 3581 §4). <see langword="null"/> when not reflected.
    /// </summary>
    public int? ObservedPublicPort { get; init; }
}

/// <summary>
/// One active SIP contact binding as reported by the registrar in a 200 OK.
/// </summary>
internal sealed class SipRegistrationBinding
{
    /// <summary>
    /// Contact URI of the registered device.
    /// </summary>
    public required string ContactUri { get; init; }

    /// <summary>
    /// Remaining registration lifetime in seconds, or -1 if not specified.
    /// </summary>
    public required int ExpiresSeconds { get; init; }
}
