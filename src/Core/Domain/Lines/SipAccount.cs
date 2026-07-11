namespace CalloraVoipSdk.Core.Domain.Lines;

/// <summary>
/// Configuration of a SIP account used to register a <see cref="IPhoneLine"/> and place/receive calls.
/// </summary>
public sealed class SipAccount
{
    /// <summary>Human-readable caller name shown to the remote party; empty by default.</summary>
    public string        DisplayName      { get; init; } = string.Empty;

    /// <summary>SIP authentication user and address user-part (required).</summary>
    public required string Username       { get; init; }

    /// <summary>
    /// SIP account password. Optional: it is only needed when the registrar challenges the
    /// registration (401/407). Leave it empty for accounts that do not authenticate — for
    /// example an IP-authenticated trunk, or a registrar that does not challenge. If a
    /// challenge does arrive and no password is set, registration fails with a clear error.
    /// </summary>
    public string        Password         { get; init; } = string.Empty;
    /// <summary>SIP registrar host (IP or FQDN) and the account's SIP domain (required).</summary>
    public required string SipServer      { get; init; }

    /// <summary>Transport used for SIP signaling; defaults to <see cref="SipTransport.Udp"/>.</summary>
    public SipTransport  Transport        { get; init; } = SipTransport.Udp;

    /// <summary>Signaling port; <c>0</c> (default) selects the standard port for the chosen <see cref="Transport"/>.</summary>
    public int           Port             { get; init; } = 0; // 0 = default per transport

    /// <summary>Requested registration lifetime in seconds; defaults to 300.</summary>
    public int           RegistrationExpiry { get; init; } = 300;

    /// <summary>Optional outbound proxy to route signaling through instead of resolving <see cref="SipServer"/> directly.</summary>
    public string?       OutboundProxy    { get; init; }

    /// <summary>
    /// Optional public host (IP or FQDN) to advertise in the REGISTER Contact and Via
    /// sent-by instead of the auto-resolved local address. Required behind NAT for public
    /// SIP trunks (e.g. sipgate), whose registrar would otherwise bind the number to an
    /// unroutable private LAN address and mark the line offline. <see langword="null"/>
    /// keeps the local address (LAN/direct scenarios).
    /// </summary>
    public string?       PublicSipHost    { get; init; }

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
