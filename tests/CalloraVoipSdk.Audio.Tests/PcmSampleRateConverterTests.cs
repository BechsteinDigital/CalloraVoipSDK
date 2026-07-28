using CalloraVoipSdk.Audio.Abstractions.Processing;

namespace CalloraVoipSdk.Audio.Tests;

/// <summary>
/// Behaviour-pinning evidence for <see cref="PcmSampleRateConverter.ConvertPcmSampleRate"/>, the
/// nearest-neighbour resampler extracted verbatim from the Linux/Windows audio devices (issue #18,
/// A8). The vectors reproduce the exact per-device algorithm so a byte drift would fail the suite.
/// </summary>
public sealed class PcmSampleRateConverterTests
{
    private static byte[] Pcm(params short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            bytes[i * 2] = (byte)(samples[i] & 0xFF);
            bytes[i * 2 + 1] = (byte)(samples[i] >> 8);
        }

        return bytes;
    }

    [Fact]
    public void Identity_rate_returns_the_same_buffer_instance()
    {
        var pcm = Pcm(1, 2, 3, 4);

        var result = PcmSampleRateConverter.ConvertPcmSampleRate(pcm, 8000, 8000);

        Assert.Same(pcm, result);
    }

    [Fact]
    public void Empty_buffer_is_returned_unchanged()
    {
        var pcm = Array.Empty<byte>();

        var result = PcmSampleRateConverter.ConvertPcmSampleRate(pcm, 8000, 16000);

        Assert.Same(pcm, result);
    }

    [Theory]
    [InlineData(0, 16000)]
    [InlineData(8000, 0)]
    [InlineData(-8000, 16000)]
    public void Non_positive_rate_returns_the_same_buffer_instance(int source, int target)
    {
        var pcm = Pcm(10, 20);

        var result = PcmSampleRateConverter.ConvertPcmSampleRate(pcm, source, target);

        Assert.Same(pcm, result);
    }

    [Fact]
    public void Upsampling_8k_to_16k_doubles_via_nearest_neighbour()
    {
        // sourceSamples = 4; targetSamples = round(4 * 16000/8000) = 8.
        // sourceIndex(i) = min(3, i*8000/16000) = i/2 → each source sample repeated once.
        var pcm = Pcm(100, 200, 300, 400);

        var result = PcmSampleRateConverter.ConvertPcmSampleRate(pcm, 8000, 16000);

        Assert.Equal(Pcm(100, 100, 200, 200, 300, 300, 400, 400), result);
    }

    [Fact]
    public void Downsampling_16k_to_8k_halves_via_nearest_neighbour()
    {
        // sourceSamples = 8; targetSamples = round(8 * 8000/16000) = 4.
        // sourceIndex(i) = min(7, i*16000/8000) = 2i → picks samples 0, 2, 4, 6.
        var pcm = Pcm(10, 11, 20, 22, 30, 33, 40, 44);

        var result = PcmSampleRateConverter.ConvertPcmSampleRate(pcm, 16000, 8000);

        Assert.Equal(Pcm(10, 20, 30, 40), result);
    }

    [Fact]
    public void Upsampling_8k_to_48k_reproduces_the_nearest_neighbour_index_mapping()
    {
        // sourceSamples = 2; targetSamples = round(2 * 48000/8000) = 12.
        // sourceIndex(i) = min(1, i*8000/48000) = min(1, i/6) → 0 for i<6, 1 for i>=6.
        var pcm = Pcm(-5, 7);

        var result = PcmSampleRateConverter.ConvertPcmSampleRate(pcm, 8000, 48000);

        Assert.Equal(Pcm(-5, -5, -5, -5, -5, -5, 7, 7, 7, 7, 7, 7), result);
    }

    [Fact]
    public void Preserves_little_endian_byte_layout_of_negative_samples()
    {
        var pcm = Pcm(short.MinValue, -1);

        var result = PcmSampleRateConverter.ConvertPcmSampleRate(pcm, 8000, 16000);

        Assert.Equal(Pcm(short.MinValue, short.MinValue, -1, -1), result);
    }
}
