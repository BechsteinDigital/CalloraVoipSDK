namespace CalloraVoipSdk.Audio.Abstractions.Processing;

/// <summary>
/// The narrow-band / wide-band audio codec a platform audio device is currently using for a call
/// leg. Shared by the platform audio devices, which previously each declared an identical private
/// copy of this enum (issue #18, A8).
/// </summary>
public enum ActiveCodec
{
    /// <summary>G.711 µ-law (PCMU, RTP payload type 0).</summary>
    Pcmu,

    /// <summary>G.711 A-law (PCMA, RTP payload type 8).</summary>
    Pcma,

    /// <summary>G.722 wide-band (RTP payload type 9).</summary>
    G722,

    /// <summary>Opus (dynamic payload type).</summary>
    Opus
}
