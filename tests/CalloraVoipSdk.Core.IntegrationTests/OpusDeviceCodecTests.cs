using CalloraVoipSdk.Core.Application.Media.Sessions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Pins the reusable Opus device codec: whole-frame encode/decode round-trip and the capture
/// accumulator that assembles sub-frame PCM blocks into valid 20 ms Opus frames. This is the shared
/// building block that makes native Opus work in the platform audio backends.
/// </summary>
public sealed class OpusDeviceCodecTests
{
    // PCM16 LE mono sine at 48 kHz so Opus has real content to encode.
    private static byte[] Pcm(int samples)
    {
        var bytes = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var s = (short)(Math.Sin(2 * Math.PI * 440 * i / 48_000) * 8_000);
            bytes[i * 2] = (byte)(s & 0xFF);
            bytes[i * 2 + 1] = (byte)(s >> 8);
        }

        return bytes;
    }

    [Fact]
    public void Full_frame_encodes_to_one_packet_that_decodes_back_to_a_frame()
    {
        var codec = new OpusDeviceCodec();

        var packets = codec.Encode(Pcm(OpusDeviceCodec.FrameSamples));

        Assert.Single(packets);
        Assert.NotEmpty(packets[0]);

        var decoded = codec.Decode(packets[0]);
        Assert.Equal(OpusDeviceCodec.FrameSamples * 2, decoded.Length);
        Assert.Contains(decoded, b => b != 0); // not silence
    }

    [Fact]
    public void Sub_frame_blocks_accumulate_into_whole_frames()
    {
        var codec = new OpusDeviceCodec();
        var half = OpusDeviceCodec.FrameSamples / 2;

        Assert.Empty(codec.Encode(Pcm(half)));   // 480 samples < one frame → buffered
        Assert.Single(codec.Encode(Pcm(half)));  // 960 total → one frame emitted
    }

    [Fact]
    public void Multi_frame_input_emits_multiple_packets()
    {
        var codec = new OpusDeviceCodec();

        var packets = codec.Encode(Pcm(OpusDeviceCodec.FrameSamples * 2));

        Assert.Equal(2, packets.Count);
    }

    [Fact]
    public void Empty_input_emits_nothing()
    {
        var codec = new OpusDeviceCodec();

        Assert.Empty(codec.Encode([]));
    }
}
