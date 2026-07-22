using NAudio.Codecs;

namespace CalloraVoipSdk.Core.Application.Media.Sessions;

/// <summary>
/// G.722 RTP payload codec (RFC 3551 PT 9) for the file/media transcoding path. G.722 is an ADPCM
/// codec whose predictor state carries across frame boundaries, so one instance owns a persistent
/// decode state and a persistent encode state for the lifetime of one call leg — re-initialising the
/// state per 20 ms frame loses the predictor history and produces audible artefacts at every frame
/// boundary. One instance therefore belongs to one stream direction; do not share across calls.
/// </summary>
internal sealed class PcmG722Codec
{
    // NAudio's G722Codec keeps no per-instance state — the ADPCM state lives entirely in
    // G722CodecState — so one codec object per direction is reused across frames (no per-frame
    // allocation) while the state it advances persists across frames. Separate decode/encode codecs
    // mirror the audio device hotpath (HARD-F1).
    private readonly G722Codec _decodeCodec = new();
    private readonly G722Codec _encodeCodec = new();
    private readonly G722CodecState _decodeState = new(64000, G722Flags.None);
    private readonly G722CodecState _encodeState = new(64000, G722Flags.None);

    /// <summary>
    /// Decodes G.722 payload bytes into PCM16 little-endian bytes, advancing the persistent decode state.
    /// </summary>
    public byte[] Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0)
            return [];

        var input = payload.ToArray();
        var samples = new short[input.Length * 2];
        _decodeCodec.Decode(_decodeState, samples, input, input.Length);

        var pcm = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, pcm, 0, pcm.Length);
        return pcm;
    }

    /// <summary>
    /// Encodes PCM16 little-endian bytes into G.722 payload bytes, advancing the persistent encode state.
    /// </summary>
    public byte[] Encode(ReadOnlySpan<byte> pcm16)
    {
        if (pcm16.Length == 0)
            return [];
        if ((pcm16.Length & 1) != 0)
            throw new InvalidOperationException("G.722 encoder expects PCM16 payload with even byte count.");

        var sampleCount = pcm16.Length / 2;
        var samples = new short[sampleCount];
        Buffer.BlockCopy(pcm16.ToArray(), 0, samples, 0, pcm16.Length);

        var encoded = new byte[Math.Max(1, sampleCount / 2)];
        _encodeCodec.Encode(_encodeState, encoded, samples, sampleCount);
        return encoded;
    }
}
