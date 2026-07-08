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
    /// Unrecognized or unsupported payload codec.
    /// </summary>
    Unknown = 6,

    /// <summary>
    /// Opus audio (RFC 7587, 48 kHz RTP clock, dynamic payload type).
    /// </summary>
    Opus = 7,
}
