namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Input model for SIP REGISTER and unREGISTER requests.
/// </summary>
internal sealed class SipRegistrationRequest
{
    /// <summary>
    /// SIP auth username and address-of-record user part.
    /// </summary>
    public required string Username { get; init; }

    /// <summary>
    /// SIP auth password.
    /// </summary>
    public required string Password { get; init; }

    /// <summary>
    /// SIP registrar host.
    /// </summary>
    public required string Domain { get; init; }

    /// <summary>
    /// SIP registrar UDP port.
    /// </summary>
    public int Port { get; init; } = 5060;

    /// <summary>
    /// Optional display-name used in the From header.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Requested registration lifetime in seconds.
    /// </summary>
    public int ExpiresSeconds { get; init; } = 300;

    /// <summary>
    /// Transaction timeout for one REGISTER operation.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// User-Agent header value.
    /// </summary>
    public string UserAgent { get; init; } = "CalloraVoipSdk/1.0";

    /// <summary>
    /// Preferred SIP signaling transport for REGISTER flows.
    /// </summary>
    public Infrastructure.Sip.Transport.SipTransportProtocol Transport { get; init; } =
        Infrastructure.Sip.Transport.SipTransportProtocol.Udp;

    /// <summary>
    /// RFC 3261 §10.2.4 – Call-ID to reuse for registration refresh.
    /// When non-null the service uses this value instead of generating a new one,
    /// which is required for re-registrations of the same binding.
    /// Pass <see cref="SipRegistrationResult.CallId"/> from the previous successful result.
    /// </summary>
    public string? ExistingCallId { get; init; }

    /// <summary>
    /// RFC 3261 §10.2.4 – CSeq number to start from when refreshing a registration.
    /// Pass <see cref="SipRegistrationResult.NextCSeq"/> from the previous successful result.
    /// Defaults to 1 when not provided (new registration).
    /// </summary>
    public int StartCSeq { get; init; } = 1;

    /// <summary>
    /// RFC 5626 §4 – Stable device instance identifier placed as the
    /// <c>+sip.instance</c> Contact parameter (e.g. <c>"urn:uuid:..."</c>).
    /// When non-null the value is quoted and appended to the Contact header.
    /// Enables a registrar to correlate re-registrations across transport flows.
    /// </summary>
    public string? InstanceId { get; init; }

    /// <summary>
    /// When <see langword="true"/> the REGISTER is sent with <c>Contact: *</c> and
    /// <c>Expires: 0</c> to remove all existing bindings (RFC 3261 §10.2.2).
    /// Username/Password are still needed for digest authentication.
    /// </summary>
    public bool WildcardContact { get; init; }

    /// <summary>
    /// When <see langword="true"/> the REGISTER is sent without a Contact header
    /// to fetch the current binding list from the registrar (RFC 3261 §10.2.3).
    /// </summary>
    public bool FetchBindings { get; init; }

    /// <summary>
    /// Optional public host (IP or FQDN) to advertise in the Contact URI and the Via
    /// sent-by host instead of the route-probed local address. Required behind NAT for
    /// public SIP trunks, whose registrar would otherwise bind the AOR to an unroutable
    /// private address. <see langword="null"/> keeps the auto-resolved local address.
    /// </summary>
    public string? PublicHost { get; init; }

    /// <summary>
    /// Optional public port to pair with <see cref="PublicHost"/> for Contact/Via.
    /// <see langword="null"/> or 0 reuses the local signaling port.
    /// </summary>
    public int? PublicPort { get; init; }
}
