using CalloraVoipSdk.Core.Application.Media.Sessions;

namespace CalloraVoipSdk.Audio.Abstractions.Processing;

/// <summary>
/// <see cref="IAudioPayloadCodec"/> wrapper over the Core-internal G.722 codec (RFC 3551 PT 9). Stateful
/// (ADPCM predictor state carries across frames) and single-direction; created via
/// <see cref="AudioPayloadCodecFactory"/>. Its PCM sample rate is 16 kHz while its RTP clock is 8 kHz — do
/// not use <see cref="PcmSampleRate"/> as the RTP clock. The NAudio types never leak out.
/// </summary>
internal sealed class G722AudioPayloadCodec : IAudioPayloadCodec
{
    private readonly PcmG722Codec _codec = new();

    /// <inheritdoc />
    public ActiveCodec Codec => ActiveCodec.G722;

    /// <inheritdoc />
    public int PcmSampleRate => 16_000;

    /// <inheritdoc />
    public byte[] DecodeToPcm16(ReadOnlySpan<byte> payload) => _codec.Decode(payload);

    /// <inheritdoc />
    public byte[] EncodeFromPcm16(ReadOnlySpan<byte> pcm16)
    {
        if ((pcm16.Length & 1) != 0)
            throw new ArgumentException("PCM16 payload must have an even byte count (2 bytes per sample).", nameof(pcm16));
        return _codec.Encode(pcm16);
    }

    /// <summary>No-op: the NAudio G722Codec / G722CodecState are managed objects with no unmanaged handle to release.</summary>
    public void Dispose() { }
}
