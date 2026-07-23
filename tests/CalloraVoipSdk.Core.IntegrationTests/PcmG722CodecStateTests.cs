using CalloraVoipSdk.Core.Application.Media.Sessions;
using NAudio.Codecs;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Regression for issue #11 (RFC 3551 G.722 ADPCM): the file/media transcoding codec must carry its
/// predictor state across frame boundaries. One <see cref="PcmG722Codec"/> instance encoding/decoding
/// a multi-frame signal must be byte-identical to a single persistent NAudio codec state driven over
/// the same frames (the oracle) — the previous per-frame fresh <c>G722CodecState</c> diverged after the
/// first frame, producing audible artefacts at every 20 ms boundary.
/// </summary>
public sealed class PcmG722CodecStateTests
{
    // G.722 is wideband: 16 kHz sample rate, 320 samples per 20 ms frame → 160 encoded bytes.
    private const int SamplesPerFrame = 320;
    private const int Frames = 100; // 2 s of audio — long enough for state drift to accumulate.

    private static short[] SineSamples(int totalSamples)
    {
        var samples = new short[totalSamples];
        for (var i = 0; i < totalSamples; i++)
            samples[i] = (short)(Math.Sin(2 * Math.PI * 440 * i / 16000.0) * 12000);
        return samples;
    }

    private static byte[] ToPcmBytes(ReadOnlySpan<short> samples)
    {
        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples.ToArray(), 0, bytes, 0, bytes.Length);
        return bytes;
    }

    [Fact]
    public void Encode_carries_state_across_frames_matching_a_single_persistent_state()
    {
        var signal = SineSamples(SamplesPerFrame * Frames);

        var codec = new PcmG722Codec();
        var oracleState = new G722CodecState(64000, G722Flags.None);
        var oracleCodec = new G722Codec();

        var actual = new List<byte>();
        var expected = new List<byte>();

        for (var f = 0; f < Frames; f++)
        {
            var frame = signal.AsSpan(f * SamplesPerFrame, SamplesPerFrame);

            actual.AddRange(codec.Encode(ToPcmBytes(frame)));

            var oracleFrame = new byte[SamplesPerFrame / 2];
            oracleCodec.Encode(oracleState, oracleFrame, frame.ToArray(), SamplesPerFrame);
            expected.AddRange(oracleFrame);
        }

        // Byte-identical to a single persistent encode state proves state is preserved across frames
        // (a fresh state per frame would diverge after the first frame).
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Decode_carries_state_across_frames_matching_a_single_persistent_state()
    {
        var signal = SineSamples(SamplesPerFrame * Frames);

        // Produce a realistic multi-frame G.722 bitstream with a single persistent encode state.
        var encodeState = new G722CodecState(64000, G722Flags.None);
        var encodeCodec = new G722Codec();
        var g722Frames = new List<byte[]>(Frames);
        for (var f = 0; f < Frames; f++)
        {
            var frame = signal.AsSpan(f * SamplesPerFrame, SamplesPerFrame).ToArray();
            var encoded = new byte[SamplesPerFrame / 2];
            encodeCodec.Encode(encodeState, encoded, frame, SamplesPerFrame);
            g722Frames.Add(encoded);
        }

        var codec = new PcmG722Codec();
        var oracleState = new G722CodecState(64000, G722Flags.None);
        var oracleCodec = new G722Codec();

        var actual = new List<byte>();
        var expected = new List<byte>();

        foreach (var g722Frame in g722Frames)
        {
            actual.AddRange(codec.Decode(g722Frame));

            var samples = new short[g722Frame.Length * 2];
            oracleCodec.Decode(oracleState, samples, g722Frame, g722Frame.Length);
            expected.AddRange(ToPcmBytes(samples));
        }

        Assert.Equal(expected, actual);
    }
}
