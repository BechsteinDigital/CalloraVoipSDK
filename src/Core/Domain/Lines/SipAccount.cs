namespace CalloraVoipSdk.Core.Domain.Lines;

/// <summary>
/// Configuration of a SIP account used to register a <see cref="IPhoneLine"/> and place/receive calls.
/// </summary>
public sealed class SipAccount
{
    /// <summary>Human-readable caller name shown to the remote party; empty by default.</summary>
    public string        DisplayName      { get; init; } = string.Empty;

    /// <summary>
    /// SIP authentication user and address user-part. Empty by default.
    /// </summary>
    /// <remarks>
    /// Required in practice for every account that registers — it is the AOR user-part the registrar binds
    /// and the name it challenges. Leave it empty only for an IP-authenticated trunk
    /// (<see cref="Register"/> = <see langword="false"/>) that has no account user at all: addresses then
    /// take the host-only form <c>sip:host</c> (RFC 3261 §19.1.1) instead of <c>sip:user@host</c>.
    /// <para>
    /// Without a user-part there is no 1:1 match for inbound calls, so <see cref="InboundNumbers"/> becomes
    /// the only way to say which calls belong to this line — see the note there.
    /// </para>
    /// </remarks>
    public string        Username         { get; init; } = string.Empty;

    /// <summary>
    /// SIP account password. Optional: it is only needed when the registrar challenges the
    /// registration (401/407). Leave it empty for accounts that do not authenticate — for
    /// example an IP-authenticated trunk, or a registrar that does not challenge. If a
    /// challenge does arrive and no password is set, registration fails with a clear error.
    /// </summary>
    public string        Password         { get; init; } = string.Empty;
    /// <summary>SIP registrar host (IP or FQDN) and the account's SIP domain (required).</summary>
    /// <exception cref="ArgumentException">The value is blank (#165 P3-11).</exception>
    public required string SipServer
    {
        get => _sipServer;
        init => _sipServer = !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException(
                "SipServer is the registrar host and SIP domain; it cannot be blank.", nameof(SipServer));
    }

    private readonly string _sipServer = string.Empty;

    /// <summary>Transport used for SIP signaling; defaults to <see cref="SipTransport.Udp"/>.</summary>
    public SipTransport  Transport        { get; init; } = SipTransport.Udp;

    /// <summary>Signaling port; <c>0</c> (default) selects the standard port for the chosen <see cref="Transport"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0..65535 (#165 P3-11).</exception>
    public int Port
    {
        get => _port;
        // 0 = default per transport; anything else must be a real port number. Rejected here rather than at
        // bind time, where it surfaces as a socket error with no hint at which account configured it.
        init => _port = value is >= 0 and <= 65535
            ? value
            : throw new ArgumentOutOfRangeException(
                nameof(Port), value, "A SIP port must be 0 (transport default) or 1..65535.");
    }

    private readonly int _port;

    /// <summary>
    /// Whether the line registers with <see cref="SipServer"/>. <see langword="true"/> by default.
    /// </summary>
    /// <remarks>
    /// Set to <see langword="false"/> for an <b>IP-authenticated static-IP trunk</b>: the provider
    /// recognises the customer by source address, no REGISTER is expected, and sending one is at best
    /// ignored and at worst rejected. The line then never sends an initial REGISTER, reaches
    /// <see cref="LineState.Ready"/> instead of <see cref="LineState.Registered"/>, and places outbound
    /// calls straight at <see cref="SipServer"/> (or <see cref="OutboundProxy"/>).
    /// <para>
    /// Not the same as <see cref="ReregisterOptions.Disabled"/>, which only stops <i>re</i>-registration
    /// after a lost binding — the initial REGISTER still goes out there.
    /// </para>
    /// <para>
    /// Inbound still works and is governed by the usual trunk rules (<see cref="AcceptTrunkInbound"/>,
    /// <see cref="InboundNumbers"/>). <see cref="RegistrationExpiry"/> and <see cref="Reregister"/> are
    /// ignored in this mode. Note that the mass-market trunks (sipgate, easybell, Telekom CompanyFlex)
    /// <i>do</i> register — leave this at the default for those.
    /// </para>
    /// </remarks>
    public bool          Register         { get; init; } = true;

    /// <summary>
    /// Requested registration lifetime in seconds; defaults to 300.
    /// Ignored when <see cref="Register"/> is <see langword="false"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive (#165 P3-11).</exception>
    public int RegistrationExpiry
    {
        get => _registrationExpiry;
        // A binding that expires immediately is not a registration; 0 is what an UNREGISTER carries.
        init => _registrationExpiry = value > 0
            ? value
            : throw new ArgumentOutOfRangeException(
                nameof(RegistrationExpiry), value, "A registration lifetime must be positive.");
    }

    private readonly int _registrationExpiry = 300;

    /// <summary>Optional outbound proxy to route signaling through instead of resolving <see cref="SipServer"/> directly.</summary>
    public string? OutboundProxy
    {
        get => _outboundProxy;
        init => _outboundProxy = value is null || !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException(
                "OutboundProxy is either unset or a host; blank is neither.", nameof(OutboundProxy));
    }

    private readonly string? _outboundProxy;

    /// <summary>
    /// Optional public host (IP or FQDN) to advertise in the REGISTER Contact and Via
    /// sent-by instead of the auto-resolved local address. Required behind NAT for public
    /// SIP trunks (e.g. sipgate), whose registrar would otherwise bind the number to an
    /// unroutable private LAN address and mark the line offline. <see langword="null"/>
    /// keeps the local address (LAN/direct scenarios).
    /// </summary>
    public string? PublicSipHost
    {
        get => _publicSipHost;
        init => _publicSipHost = value is null || !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException(
                "PublicSipHost is either unset or a host; blank is neither.", nameof(PublicSipHost));
    }

    private readonly string? _publicSipHost;

    /// <summary>
    /// Optional public signaling port paired with <see cref="PublicSipHost"/>. Use when a
    /// NAT port mapping differs from the local port. <see langword="null"/> or 0 reuses
    /// the local signaling port.
    /// </summary>
    public int?          PublicSipPort    { get; init; }

    /// <summary>
    /// Optional public IP address to force into the SDP media connection line (<c>c=</c>) and RTP
    /// bind for calls on this line. By default the media address is auto-resolved from the OS
    /// routing table and NAT is handled by symmetric RTP; set this only behind CGNAT / a static
    /// 1:1 NAT with port preservation where the peer does not latch to the source address.
    /// Must be an IP literal; non-IP values are ignored. <see langword="null"/> keeps the
    /// auto-resolved media address (default, unchanged behavior).
    /// </summary>
    public string?       PublicMediaHost  { get; init; }

    /// <summary>
    /// Optional inbound number (DID) whitelist for SIP trunks. When set, the line only
    /// accepts inbound INVITEs whose called number (To user-part on the registered domain)
    /// is in this list — useful to disambiguate multiple lines on the same provider domain.
    /// When <see langword="null"/> or empty, the line accepts calls for its exact username,
    /// calls delivered by the registrar it registered to, and any number on its registered
    /// domain (trunk default). <see cref="Username"/>-only accounts are unaffected.
    /// </summary>
    /// <remarks>
    /// <b>Required when <see cref="Username"/> is empty.</b> The username is what gives a line its exact
    /// 1:1 match; without it the only remaining rule is "anything on this domain", so a line would answer
    /// every inbound call the provider sends — including those meant for a different line on the same
    /// domain. Connecting such an account is refused rather than silently over-accepting.
    /// </remarks>
    public IReadOnlyList<string>? InboundNumbers { get; init; }

    /// <summary>
    /// Whether the line accepts inbound INVITEs delivered by its registrar/proxy peer or,
    /// only when the source is unknown, addressed to its registered domain (SIP-trunk
    /// behavior). When <see langword="true"/> (default) a call for the exact username is
    /// always accepted, plus — when no <see cref="InboundNumbers"/> whitelist is set — calls
    /// from the trusted registrar peer and domain-addressed calls from an unknown source.
    /// Set to <see langword="false"/> for a strict 1:1 user account that must accept only its
    /// own username. Ignored when <see cref="InboundNumbers"/> is set (whitelist wins).
    /// </summary>
    public bool AcceptTrunkInbound { get; init; } = true;

    /// <summary>
    /// Controls automatic re-registration when the SIP binding is lost.
    /// Defaults to <see cref="ReregisterOptions.Default"/> (unlimited retries, exponential backoff).
    /// </summary>
    public ReregisterOptions Reregister   { get; init; } = ReregisterOptions.Default;

    /// <summary>
    /// Checks what an individual property initialiser cannot see on its own (#165 P3-11) — currently the
    /// re-registration backoff window, whose two ends are only comparable once both are set. Called when the
    /// line that uses this account is built, so a contradictory configuration is rejected before any REGISTER
    /// goes out instead of surfacing much later as odd retry behaviour.
    /// </summary>
    internal void Validate() => Reregister.Validate();

    /// <summary>The account's SIP address-of-record, derived as <c>sip:Username@SipServer</c>.</summary>
    public SipAddress Address =>
        SipAddress.From(Username, SipServer);

    /// <summary>The authentication credentials derived from <see cref="Username"/> and <see cref="Password"/>.</summary>
    public SipCredentials Credentials =>
        new(Username, Password);

    /// <summary>The port actually used: <see cref="Port"/> when non-zero, otherwise the default for <see cref="Transport"/> (5060 UDP/TCP, 5061 TLS, 80 WS, 443 WSS).</summary>
    public int EffectivePort => Port > 0 ? Port : Transport switch
    {
        SipTransport.Tls => 5061,
        SipTransport.Ws => 80,
        SipTransport.Wss => 443,
        _ => 5060
    };
}
