namespace CalloraVoipSdk;

/// <summary>
/// Default SIP signaling transport a consumer selects via
/// <see cref="VoipConfiguration.DefaultTransport"/>. Determines which transport the SDK uses for
/// outbound requests and its advertised local contact when a target URI does not force a transport
/// (for example via a <c>sips:</c> scheme or <c>;transport=</c> parameter). All five transports
/// are listened on regardless; this only selects the default for outbound routing.
/// </summary>
public enum SipTransport
{
    /// <summary>SIP over UDP (RFC 3261). The default.</summary>
    Udp = 0,

    /// <summary>SIP over TCP (RFC 3261).</summary>
    Tcp = 1,

    /// <summary>SIP over TLS / SIPS (RFC 3261 §26.2).</summary>
    Tls = 2,

    /// <summary>SIP over WebSocket (RFC 7118).</summary>
    Ws = 3,

    /// <summary>SIP over secure WebSocket (RFC 7118).</summary>
    Wss = 4
}
