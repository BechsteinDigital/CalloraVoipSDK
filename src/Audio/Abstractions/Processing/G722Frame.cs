using NAudio.Codecs;

namespace CalloraVoipSdk.Audio.Abstractions.Processing;

/// <summary>
/// Encodes and decodes a single G.722 frame on the audio hotpath. The caller owns the
/// <see cref="G722Codec"/> instance and the per-stream <see cref="G722CodecState"/> (both cached and
/// reused per frame); this helper only performs the buffer marshalling. Shared by the platform audio
/// devices, which previously duplicated this logic byte-for-byte (issue #18, A8).
/// </summary>
public static class G722Frame
{
    /// <summary>
    /// Encodes a 16-bit little-endian PCM frame to G.722. Returns an empty array when
    /// <paramref name="state"/> is <see langword="null"/> (the stream is not yet initialised).
    /// </summary>
    /// <param name="codec">The cached G.722 codec instance.</param>
    /// <param name="state">The per-stream encoder state, or <see langword="null"/> when uninitialised.</param>
    /// <param name="pcm">The 16-bit little-endian PCM frame to encode.</param>
    /// <returns>The encoded G.722 payload, or an empty array when the stream is uninitialised.</returns>
    public static byte[] Encode(G722Codec codec, G722CodecState? state, byte[] pcm)
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(pcm);

        if (state is null)
            return Array.Empty<byte>();

        var sampleCount = pcm.Length / 2;
        var samples = new short[sampleCount];
        Buffer.BlockCopy(pcm, 0, samples, 0, pcm.Length);

        var encoded = new byte[Math.Max(1, sampleCount / 2)];
        codec.Encode(state, encoded, samples, sampleCount);
        return encoded;
    }

    /// <summary>
    /// Decodes a G.722 payload to a 16-bit little-endian PCM frame. Returns an empty array when
    /// <paramref name="state"/> is <see langword="null"/> (the stream is not yet initialised).
    /// </summary>
    /// <param name="codec">The cached G.722 codec instance.</param>
    /// <param name="state">The per-stream decoder state, or <see langword="null"/> when uninitialised.</param>
    /// <param name="payload">The G.722 payload to decode.</param>
    /// <returns>The decoded PCM frame, or an empty array when the stream is uninitialised.</returns>
    public static byte[] Decode(G722Codec codec, G722CodecState? state, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(payload);

        if (state is null)
            return Array.Empty<byte>();

        var samples = new short[payload.Length * 2];
        codec.Decode(state, samples, payload, payload.Length);

        var pcm = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, pcm, 0, pcm.Length);
        return pcm;
    }
}
