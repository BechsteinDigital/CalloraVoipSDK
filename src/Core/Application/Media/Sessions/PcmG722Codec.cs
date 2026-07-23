using System.Runtime.InteropServices;
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

    // Grow-on-demand scratch buffers reused across frames. The NAudio ADPCM API needs a byte[]/short[]
    // pair, so these hold the materialised input and the intermediate samples the previous implementation
    // re-allocated per frame (payload.ToArray() / new short[...]). Safe to reuse because one instance
    // serves one stream direction on one thread (see the class remark); only the returned array is freshly
    // allocated each call, because the caller retains it as a MediaFrame payload.
    private byte[] _decodeInput = [];
    private short[] _decodeSamples = [];
    private short[] _encodeSamples = [];

    /// <summary>
    /// Decodes G.722 payload bytes into PCM16 little-endian bytes, advancing the persistent decode state.
    /// </summary>
    public byte[] Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0)
            return [];

        var input = EnsureCapacity(ref _decodeInput, payload.Length);
        payload.CopyTo(input);

        var sampleCount = payload.Length * 2;
        var samples = EnsureCapacity(ref _decodeSamples, sampleCount);
        _decodeCodec.Decode(_decodeState, samples, input, payload.Length);

        var pcm = new byte[sampleCount * 2];
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
        var samples = EnsureCapacity(ref _encodeSamples, sampleCount);
        // Reinterpret the PCM16 bytes as samples in host byte order — identical to the previous
        // Buffer.BlockCopy, without the intermediate byte[] copy pcm16.ToArray() allocated.
        MemoryMarshal.Cast<byte, short>(pcm16).CopyTo(samples);

        var encoded = new byte[Math.Max(1, sampleCount / 2)];
        _encodeCodec.Encode(_encodeState, encoded, samples, sampleCount);
        return encoded;
    }

    // Returns a buffer of at least minLength, replacing the field only when the current one is too small
    // (frames are a fixed size in practice, so this grows at most once).
    private static T[] EnsureCapacity<T>(ref T[] buffer, int minLength)
    {
        if (buffer.Length < minLength)
            buffer = new T[minLength];
        return buffer;
    }
}
