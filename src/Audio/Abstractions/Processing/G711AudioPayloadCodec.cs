using CalloraVoipSdk.Core.Application.Media.Sessions;

namespace CalloraVoipSdk.Audio.Abstractions.Processing;

/// <summary>
/// <see cref="IAudioPayloadCodec"/> wrapper over the Core-internal G.711 codec (µ-law / A-law, 8 kHz). The
/// codec is stateless, but the one-instance-per-direction contract applies uniformly. Created via
/// <see cref="AudioPayloadCodecFactory"/>.
/// </summary>
internal sealed class G711AudioPayloadCodec : IAudioPayloadCodec
{
    private readonly bool _aLaw;

    /// <summary>Creates the G.711 transcoder for <paramref name="codec"/> (<see cref="ActiveCodec.Pcma"/> or <see cref="ActiveCodec.Pcmu"/>).</summary>
    /// <param name="codec">The G.711 variant.</param>
    public G711AudioPayloadCodec(ActiveCodec codec)
    {
        _aLaw = codec == ActiveCodec.Pcma;
        Codec = codec;
    }

    /// <inheritdoc />
    public ActiveCodec Codec { get; }

    /// <inheritdoc />
    public int PcmSampleRate => 8_000;

    /// <inheritdoc />
    public byte[] DecodeToPcm16(ReadOnlySpan<byte> payload)
        => _aLaw ? PcmG711Codec.DecodeALaw(payload) : PcmG711Codec.DecodeMuLaw(payload);

    /// <inheritdoc />
    public byte[] EncodeFromPcm16(ReadOnlySpan<byte> pcm16)
    {
        if ((pcm16.Length & 1) != 0)
            throw new ArgumentException("PCM16 payload must have an even byte count (2 bytes per sample).", nameof(pcm16));
        return _aLaw ? PcmG711Codec.EncodeALaw(pcm16) : PcmG711Codec.EncodeMuLaw(pcm16);
    }

    /// <summary>No-op: the G.711 codec is stateless — nothing to release.</summary>
    public void Dispose() { }
}
