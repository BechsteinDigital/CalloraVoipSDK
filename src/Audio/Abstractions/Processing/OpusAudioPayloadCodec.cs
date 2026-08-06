using CalloraVoipSdk.Core.Application.Media.Sessions;

namespace CalloraVoipSdk.Audio.Abstractions.Processing;

/// <summary>
/// <see cref="IAudioPayloadCodec"/> wrapper over the Core-internal Opus payload codec (RFC 7587). Stateful
/// and single-direction; created via <see cref="AudioPayloadCodecFactory"/>. The Concentus types never leak
/// out — input and output are PCM16 little-endian bytes.
/// </summary>
internal sealed class OpusAudioPayloadCodec : IAudioPayloadCodec
{
    private readonly OpusPayloadCodec _codec;
    private readonly HashSet<int> _validFrameSampleCounts;

    /// <summary>Creates the Opus transcoder at <paramref name="pcmSampleRate"/> Hz (a supported Opus rate).</summary>
    /// <param name="pcmSampleRate">The PCM sample rate this instance decodes to and encodes from.</param>
    public OpusAudioPayloadCodec(int pcmSampleRate)
    {
        _codec = new OpusPayloadCodec(pcmSampleRate);
        PcmSampleRate = pcmSampleRate;
        // Opus accepts only 2.5/5/10/20/40/60 ms frames (RFC 7587) — the sample counts at this rate.
        _validFrameSampleCounts =
        [
            pcmSampleRate / 400, pcmSampleRate / 200, pcmSampleRate / 100,
            pcmSampleRate / 50, pcmSampleRate / 25, pcmSampleRate * 3 / 50,
        ];
    }

    /// <inheritdoc />
    public ActiveCodec Codec => ActiveCodec.Opus;

    /// <inheritdoc />
    public int PcmSampleRate { get; }

    /// <inheritdoc />
    public byte[] DecodeToPcm16(ReadOnlySpan<byte> payload) => _codec.Decode(payload);

    /// <inheritdoc />
    public byte[] EncodeFromPcm16(ReadOnlySpan<byte> pcm16)
    {
        if (pcm16.Length == 0)
            return [];
        if ((pcm16.Length & 1) != 0)
            throw new ArgumentException("PCM16 payload must have an even byte count (2 bytes per sample).", nameof(pcm16));

        var sampleCount = pcm16.Length / 2;
        if (!_validFrameSampleCounts.Contains(sampleCount))
            throw new ArgumentException(
                $"{sampleCount} samples is not a valid Opus frame length at {PcmSampleRate} Hz " +
                "(only 2.5/5/10/20/40/60 ms frames are allowed).", nameof(pcm16));

        return _codec.Encode(pcm16);
    }

    /// <summary>No-op: the Concentus encoder/decoder are managed objects with no unmanaged handle to release.</summary>
    public void Dispose() { }
}
