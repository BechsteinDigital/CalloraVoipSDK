namespace CalloraVoipSdk.Audio.Abstractions.Processing;

/// <summary>
/// A stateful, single-direction audio payload transcoder between an RTP payload (Opus/G.711/G.722) and
/// linear PCM16 (little-endian, host byte order). It lets a server-side consumer — e.g. an SFU bridging a
/// telephone leg into a WebRTC conference — decode received payloads to PCM, mix in PCM, and re-encode to
/// the outbound codec without binding a native codec library itself (#205).
/// <para>
/// <b>Statefulness.</b> Opus and G.722 carry predictor/FEC state across frames, so one instance belongs to
/// exactly <em>one</em> stream direction (decode <em>or</em> encode is driven per instance across the whole
/// stream, never shared between two directions or two calls). Sharing an instance across directions produces
/// hard-to-diagnose artefacts. G.711 is stateless, but the same one-instance-per-direction contract applies
/// uniformly. Create one instance per direction with <see cref="AudioPayloadCodecFactory"/> and dispose it
/// when the stream ends.
/// </para>
/// <para>
/// <b>PCM sample rate vs. RTP clock.</b> <see cref="PcmSampleRate"/> is the rate of the PCM16 this instance
/// produces and consumes — <em>not</em> the RTP timestamp clock. For G.722 the two differ: its PCM sample
/// rate is 16 kHz while its RTP clock is 8 kHz (RFC 3551 §4.5.2), so never use <see cref="PcmSampleRate"/> as
/// the RTP clock. For Opus the RTP clock is always 48 kHz (RFC 7587 §4.1) regardless of the PCM rate.
/// </para>
/// </summary>
public interface IAudioPayloadCodec : IDisposable
{
    /// <summary>The codec this instance transcodes.</summary>
    ActiveCodec Codec { get; }

    /// <summary>
    /// The sample rate, in Hz, of the PCM16 this instance decodes to and encodes from. This is the media
    /// sample rate, not the RTP timestamp clock (they differ for G.722 — see the type remarks).
    /// </summary>
    int PcmSampleRate { get; }

    /// <summary>
    /// Decodes one received RTP payload into PCM16 little-endian bytes at <see cref="PcmSampleRate"/>.
    /// Advances the decoder state for the stateful codecs. An empty payload yields an empty array.
    /// </summary>
    /// <param name="payload">The encoded RTP payload for one packet.</param>
    /// <returns>PCM16 little-endian samples (2 bytes per sample).</returns>
    byte[] DecodeToPcm16(ReadOnlySpan<byte> payload);

    /// <summary>
    /// Encodes PCM16 little-endian bytes at <see cref="PcmSampleRate"/> into one RTP payload. Advances the
    /// encoder state for the stateful codecs. An empty input yields an empty array.
    /// </summary>
    /// <param name="pcm16">PCM16 little-endian samples (2 bytes per sample).</param>
    /// <returns>The encoded RTP payload.</returns>
    /// <exception cref="ArgumentException">
    /// The PCM byte count is odd, or (for Opus) is not a valid Opus frame length
    /// (2.5/5/10/20/40/60 ms at <see cref="PcmSampleRate"/>).
    /// </exception>
    byte[] EncodeFromPcm16(ReadOnlySpan<byte> pcm16);
}
