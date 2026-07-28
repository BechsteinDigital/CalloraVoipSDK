using CalloraVoipSdk.Audio.Abstractions.Processing;
using NAudio.Codecs;

namespace CalloraVoipSdk.Audio.Tests;

/// <summary>
/// Behaviour-pinning evidence for <see cref="G722Frame"/>, the G.722 frame marshalling extracted
/// verbatim from the Linux/Windows audio devices (issue #18, A8). The reference vectors are produced
/// by the exact inline logic the devices used before the extraction, proving byte-for-byte identity.
/// </summary>
public sealed class G722FrameTests
{
    // 20 ms wide-band frame: 320 16-bit samples encode to 160 G.722 bytes.
    private const int SamplesPerFrame = 320;

    private static byte[] PcmFrame(int seed)
    {
        var bytes = new byte[SamplesPerFrame * 2];
        for (var i = 0; i < SamplesPerFrame; i++)
        {
            var sample = (short)((i * 31 + seed * 7) & 0x7FFF);
            bytes[i * 2] = (byte)(sample & 0xFF);
            bytes[i * 2 + 1] = (byte)(sample >> 8);
        }

        return bytes;
    }

    // The pre-extraction inline encoder, reproduced verbatim for byte-identity comparison.
    private static byte[] ReferenceEncode(G722Codec codec, G722CodecState state, byte[] pcm)
    {
        var sampleCount = pcm.Length / 2;
        var samples = new short[sampleCount];
        System.Buffer.BlockCopy(pcm, 0, samples, 0, pcm.Length);
        var encoded = new byte[System.Math.Max(1, sampleCount / 2)];
        codec.Encode(state, encoded, samples, sampleCount);
        return encoded;
    }

    // The pre-extraction inline decoder, reproduced verbatim for byte-identity comparison.
    private static byte[] ReferenceDecode(G722Codec codec, G722CodecState state, byte[] payload)
    {
        var samples = new short[payload.Length * 2];
        codec.Decode(state, samples, payload, payload.Length);
        var pcm = new byte[samples.Length * 2];
        System.Buffer.BlockCopy(samples, 0, pcm, 0, pcm.Length);
        return pcm;
    }

    [Fact]
    public void Encode_is_byte_identical_to_the_pre_extraction_inline_path()
    {
        const int frames = 25;
        var helperState = new G722CodecState(64000, G722Flags.None);
        var referenceState = new G722CodecState(64000, G722Flags.None);
        var helperCodec = new G722Codec();
        var referenceCodec = new G722Codec();

        for (var f = 0; f < frames; f++)
        {
            var pcm = PcmFrame(f);

            var helper = G722Frame.Encode(helperCodec, helperState, pcm);
            var reference = ReferenceEncode(referenceCodec, referenceState, pcm);

            Assert.Equal(reference, helper);
        }
    }

    [Fact]
    public void Decode_is_byte_identical_to_the_pre_extraction_inline_path()
    {
        const int frames = 25;
        var encodeState = new G722CodecState(64000, G722Flags.None);
        var encodeCodec = new G722Codec();
        var helperState = new G722CodecState(64000, G722Flags.None);
        var referenceState = new G722CodecState(64000, G722Flags.None);
        var helperCodec = new G722Codec();
        var referenceCodec = new G722Codec();

        for (var f = 0; f < frames; f++)
        {
            var payload = G722Frame.Encode(encodeCodec, encodeState, PcmFrame(f));

            var helper = G722Frame.Decode(helperCodec, helperState, payload);
            var reference = ReferenceDecode(referenceCodec, referenceState, payload);

            Assert.Equal(reference, helper);
        }
    }

    [Fact]
    public void Roundtrip_preserves_frame_length()
    {
        var encodeState = new G722CodecState(64000, G722Flags.None);
        var decodeState = new G722CodecState(64000, G722Flags.None);
        var codec = new G722Codec();
        var pcm = PcmFrame(3);

        var encoded = G722Frame.Encode(codec, encodeState, pcm);
        var decoded = G722Frame.Decode(codec, decodeState, encoded);

        Assert.Equal(SamplesPerFrame / 2, encoded.Length);
        Assert.Equal(pcm.Length, decoded.Length);
    }

    [Fact]
    public void Null_state_returns_empty_without_touching_the_codec()
    {
        var codec = new G722Codec();

        Assert.Empty(G722Frame.Encode(codec, state: null, PcmFrame(1)));
        Assert.Empty(G722Frame.Decode(codec, state: null, new byte[160]));
    }
}
