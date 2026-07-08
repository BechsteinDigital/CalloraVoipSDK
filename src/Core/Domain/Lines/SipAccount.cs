namespace CalloraVoipSdk.Core.Domain.Lines;

public sealed class SipAccount
{
    public string        DisplayName      { get; init; } = string.Empty;
    public required string Username       { get; init; }
    public required string Password       { get; init; }
    public required string SipServer      { get; init; }
    public SipTransport  Transport        { get; init; } = SipTransport.Udp;
    public int           Port             { get; init; } = 0; // 0 = default per transport
    public int           RegistrationExpiry { get; init; } = 300;
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

    public SipAddress Address =>
        SipAddress.From(Username, SipServer);

    public SipCredentials Credentials =>
        new(Username, Password);

    public int EffectivePort => Port > 0 ? Port : Transport switch
    {
        SipTransport.Tls => 5061,
        SipTransport.Ws => 80,
        SipTransport.Wss => 443,
        _ => 5060
    };
}
