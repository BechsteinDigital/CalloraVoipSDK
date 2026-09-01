namespace CalloraVoipSdk.Core.Application.Media.Sessions;

/// <summary>
/// Normalized payload codec family used by media file transcoding.
/// </summary>
internal enum PayloadCodecKind
{
    /// <summary>
    /// Linear PCM16 little-endian audio.
    /// </summary>
    Pcm16 = 0,

    /// <summary>
    /// G.711 µ-law audio.
    /// </summary>
    Pcmu = 1,

    /// <summary>
    /// G.711 A-law audio.
    /// </summary>
    Pcma = 2,

    /// <summary>
    /// MPEG audio bitstream.
    /// </summary>
    Mp3 = 3,

    /// <summary>
    /// G.722 wideband ADPCM audio.
    /// </summary>
    G722 = 4,

    /// <summary>
    /// RFC 3389 comfort-noise payload.
    /// </summary>
    ComfortNoise = 5,

    /// <summary>
    /// G.729 (RTP payload type 18).
    /// </summary>
    /// <remarks>
    /// Named rather than left to <see cref="Unknown"/>, and that is the entire point of the value: this
    /// SDK carries no G.729 implementation, so it can negotiate the format and forward it untouched but
    /// never decode it. Recognising it turns "some codec we could not transcode" into a sentence an
    /// operator can act on, and stops it from being confused with a codec nobody has heard of.
    /// <para>
    /// Carrying one would be possible — SIPSorcery ships a port of the ITU reference under Apache-2.0 —
    /// and it is a decision about scope rather than about rights. Until it is made, forwarding is the
    /// honest half.
    /// </para>
    /// </remarks>
    G729 = 8,

    /// <summary>
    /// Unrecognized or unsupported payload codec.
    /// </summary>
    Unknown = 6,

    /// <summary>
    /// Opus audio (RFC 7587, 48 kHz RTP clock, dynamic payload type).
    /// </summary>
    Opus = 7,
}
