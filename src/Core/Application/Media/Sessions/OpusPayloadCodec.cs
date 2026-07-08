using Concentus;
using Concentus.Enums;

namespace CalloraVoipSdk.Core.Application.Media.Sessions;

/// <summary>
/// Opus RTP payload codec (RFC 7587) backed by the managed Concentus implementation —
/// no native binaries, which keeps SDK consumers free of per-platform deployment work.
/// Operates mono at the full 48 kHz Opus RTP clock; the decoder downmixes stereo
/// payloads (e.g. trunk offers announcing <c>opus/48000/2</c>) per the Opus spec.
/// Encoder and decoder are stateful across frames (prediction/FEC), so one instance
/// belongs to one stream direction — do not share across calls.
/// </summary>
internal sealed class OpusPayloadCodec
{
    /// <summary>Opus RTP clock rate — always 48000 regardless of coded bandwidth (RFC 7587 §4.1).</summary>
    public const int RtpClockRate = 48_000;

    /// <summary>Samples per 20 ms frame at the RTP clock (the telephony default ptime).</summary>
    public const int SamplesPerDefaultFrame = 960;

    // 120 ms at 48 kHz — the maximum Opus frame duration a single packet may carry.
    private const int MaxDecodedSamples = 5_760;
    private const int MaxEncodedBytes = 1_275;

    private readonly IOpusEncoder _encoder;
    private readonly IOpusDecoder _decoder;

    /// <summary>Creates one codec instance for one media stream.</summary>
    public OpusPayloadCodec()
    {
        _encoder = OpusCodecFactory.CreateEncoder(RtpClockRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        _decoder = OpusCodecFactory.CreateDecoder(RtpClockRate, 1);
    }

    /// <summary>
    /// Decodes one Opus payload into PCM16 little-endian bytes (mono, 48 kHz).
    /// </summary>
    public byte[] Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0)
            return [];

        var samples = new short[MaxDecodedSamples];
        var decoded = _decoder.Decode(payload, samples, MaxDecodedSamples, false);

        var pcm = new byte[decoded * 2];
        Buffer.BlockCopy(samples, 0, pcm, 0, pcm.Length);
        return pcm;
    }

    /// <summary>
    /// Encodes PCM16 little-endian bytes (mono, 48 kHz) into one Opus payload.
    /// The sample count must be a valid Opus frame size (2.5/5/10/20/40/60 ms,
    /// i.e. 120–2880 samples at 48 kHz); the media pipeline's 20 ms frames (960) are.
    /// </summary>
    public byte[] Encode(ReadOnlySpan<byte> pcm16)
    {
        if (pcm16.Length == 0)
            return [];
        if ((pcm16.Length & 1) != 0)
            throw new InvalidOperationException("Opus encoder expects PCM16 payload with even byte count.");

        var sampleCount = pcm16.Length / 2;
        var samples = new short[sampleCount];
        Buffer.BlockCopy(pcm16.ToArray(), 0, samples, 0, pcm16.Length);

        var output = new byte[MaxEncodedBytes];
        var written = _encoder.Encode(samples, sampleCount, output, output.Length);
        return output[..written];
    }
}
