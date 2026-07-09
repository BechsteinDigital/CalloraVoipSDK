namespace CalloraVoipSdk;

/// <summary>
/// Audio format delivered to and expected from a media consumer (the "bridge"/tap), such as
/// an AI realtime bridge. When it differs from the negotiated wire codec, the SDK transcodes
/// audio transparently so the consumer sees a single, fixed codec regardless of what the SIP
/// peer negotiated.
/// </summary>
public enum BridgeAudioFormat
{
    /// <summary>
    /// Deliver and send the raw negotiated wire payload unchanged (default). The consumer must
    /// handle whatever codec was negotiated.
    /// </summary>
    Passthrough = 0,

    /// <summary>
    /// Always deliver and accept G.711 µ-law (PCMU, 8 kHz). Inbound wire audio is transcoded
    /// to µ-law and outbound µ-law is transcoded to the wire codec. Supported wire codecs:
    /// Opus and G.711 (µ-law/A-law); other codecs are delivered as passthrough with a warning.
    /// </summary>
    Pcmu = 1
}
