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
    /// Optional inbound number (DID) whitelist for SIP trunks. When set, the line only
    /// accepts inbound INVITEs whose called number (To user-part on the registered domain)
    /// is in this list — useful to disambiguate multiple lines on the same provider domain.
    /// When <see langword="null"/> or empty, the line accepts calls for its exact username,
    /// calls delivered by the registrar it registered to, and any number on its registered
    /// domain (trunk default). <see cref="Username"/>-only accounts are unaffected.
    /// </summary>
    public IReadOnlyList<string>? InboundNumbers { get; init; }

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
