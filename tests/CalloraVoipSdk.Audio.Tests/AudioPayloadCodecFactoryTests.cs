using System;
using CalloraVoipSdk.Audio.Abstractions.Processing;
using Xunit;

namespace CalloraVoipSdk.Audio.Tests;

/// <summary>
/// [Audio] #205: the public <see cref="IAudioPayloadCodec"/> transcoding surface must let a server-side
/// consumer decode a received payload to PCM16 and re-encode it, per codec, without binding a native codec
/// library. Pins the factory mapping, the per-codec round-trip wiring (decode/encode not swapped) and the
/// fail-closed argument validation (odd PCM, invalid Opus frame length, wrong fixed rate, unsupported rate).
/// </summary>
public sealed class AudioPayloadCodecFactoryTests
{
    [Theory]
    [InlineData(ActiveCodec.Pcmu, 8_000)]
    [InlineData(ActiveCodec.Pcma, 8_000)]
    [InlineData(ActiveCodec.G722, 16_000)]
    [InlineData(ActiveCodec.Opus, 48_000)]
    public void Create_reports_the_codec_and_its_canonical_pcm_sample_rate(ActiveCodec codec, int expectedRate)
    {
        using var payloadCodec = AudioPayloadCodecFactory.Create(codec);

        Assert.Equal(codec, payloadCodec.Codec);
        Assert.Equal(expectedRate, payloadCodec.PcmSampleRate);
    }

    [Theory]
    [InlineData(ActiveCodec.Pcmu)]
    [InlineData(ActiveCodec.Pcma)]
    [InlineData(ActiveCodec.G722)]
    [InlineData(ActiveCodec.Opus)]
    public void A_pcm_frame_round_trips_through_encode_then_decode(ActiveCodec codec)
    {
        using var payloadCodec = AudioPayloadCodecFactory.Create(codec);

        // One 20 ms frame of a non-silent tone at the codec's PCM rate.
        var samplesPerFrame = payloadCodec.PcmSampleRate / 50;
        var pcm = BuildTone(samplesPerFrame);

        var payload = payloadCodec.EncodeFromPcm16(pcm);
        Assert.NotEmpty(payload);                 // encode produced an RTP payload
        Assert.True(payload.Length < pcm.Length);  // and it is compressed relative to the PCM input

        var decoded = payloadCodec.DecodeToPcm16(payload);
        Assert.NotEmpty(decoded);                  // decode produced PCM back
        Assert.Equal(0, decoded.Length % 2);       // whole PCM16 samples
        Assert.Contains(decoded, b => b != 0);     // and it is not silence (decode/encode are not swapped)
    }

    [Fact]
    public void Empty_input_yields_empty_output_on_both_directions()
    {
        using var codec = AudioPayloadCodecFactory.Create(ActiveCodec.Opus);

        Assert.Empty(codec.EncodeFromPcm16(ReadOnlySpan<byte>.Empty));
        Assert.Empty(codec.DecodeToPcm16(ReadOnlySpan<byte>.Empty));
    }

    [Theory]
    [InlineData(ActiveCodec.Pcmu)]
    [InlineData(ActiveCodec.Pcma)]
    [InlineData(ActiveCodec.G722)]
    [InlineData(ActiveCodec.Opus)]
    public void Encode_rejects_an_odd_pcm_byte_count(ActiveCodec codec)
    {
        using var payloadCodec = AudioPayloadCodecFactory.Create(codec);

        Assert.Throws<ArgumentException>(() => payloadCodec.EncodeFromPcm16(new byte[3]));
    }

    [Fact]
    public void Opus_encode_rejects_an_invalid_frame_length_with_a_named_error()
    {
        using var opus = AudioPayloadCodecFactory.Create(ActiveCodec.Opus);

        // 500 samples (1000 bytes) at 48 kHz is ~10.4 ms — not a valid Opus frame duration.
        var ex = Assert.Throws<ArgumentException>(() => opus.EncodeFromPcm16(new byte[1000]));
        Assert.Contains("Opus frame", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Opus_encode_accepts_every_valid_frame_length()
    {
        using var opus = AudioPayloadCodecFactory.Create(ActiveCodec.Opus);

        // 2.5/5/10/20/40/60 ms at 48 kHz.
        foreach (var samples in new[] { 120, 240, 480, 960, 1920, 2880 })
            Assert.NotNull(opus.EncodeFromPcm16(BuildTone(samples)));
    }

    [Theory]
    [InlineData(ActiveCodec.G722, 8_000)]   // G.722 is fixed 16 kHz
    [InlineData(ActiveCodec.Pcmu, 16_000)]  // G.711 is fixed 8 kHz
    [InlineData(ActiveCodec.Pcma, 48_000)]
    public void Create_rejects_a_non_canonical_rate_for_a_fixed_rate_codec(ActiveCodec codec, int rate)
    {
        Assert.Throws<ArgumentException>(() => AudioPayloadCodecFactory.Create(codec, rate));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(44_100)] // a real rate, but not one Opus supports
    public void Create_rejects_an_unsupported_opus_rate(int rate)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioPayloadCodecFactory.Create(ActiveCodec.Opus, rate));
    }

    [Fact]
    public void Opus_can_be_created_at_a_telephony_rate()
    {
        using var opus = AudioPayloadCodecFactory.Create(ActiveCodec.Opus, 8_000);

        Assert.Equal(8_000, opus.PcmSampleRate);
        Assert.NotEmpty(opus.EncodeFromPcm16(BuildTone(160))); // 20 ms at 8 kHz
    }

    private static byte[] BuildTone(int sampleCount)
    {
        var pcm = new byte[sampleCount * 2];
        for (var i = 0; i < sampleCount; i++)
        {
            // A modest-amplitude sine so every codec has real signal to quantise (never digital silence).
            var sample = (short)(8000 * Math.Sin(2 * Math.PI * 3 * i / sampleCount));
            pcm[i * 2] = (byte)(sample & 0xFF);
            pcm[(i * 2) + 1] = (byte)((sample >> 8) & 0xFF);
        }

        return pcm;
    }
}
