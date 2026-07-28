using System.Collections.Generic;
using CalloraVoipSdk.Audio.Abstractions.Processing;

namespace CalloraVoipSdk.Audio.Tests;

/// <summary>
/// Behaviour-pinning evidence for <see cref="AudioCodecResolver"/>, the codec-resolution logic
/// extracted verbatim from the Linux/Windows audio devices (issue #18, A8).
/// </summary>
public sealed class AudioCodecResolverTests
{
    private static readonly IReadOnlyDictionary<int, string> Empty = new Dictionary<int, string>();

    [Theory]
    [InlineData(0, "PCMU")]
    [InlineData(8, "PCMA")]
    [InlineData(9, "G722")]
    public void Static_payload_types_resolve_to_their_codec(int payloadType, string expected)
    {
        var codec = AudioCodecResolver.ResolveActiveCodec(payloadType, sampleRate: 0, codecName: "", Empty);

        Assert.Equal(Parse(expected), codec);
    }

    [Fact]
    public void Sample_rate_at_or_above_16k_resolves_to_g722_for_unknown_payload_type()
    {
        var codec = AudioCodecResolver.ResolveActiveCodec(96, sampleRate: 16000, codecName: "", Empty);

        Assert.Equal(ActiveCodec.G722, codec);
    }

    [Fact]
    public void Unknown_payload_type_below_16k_defaults_to_pcmu()
    {
        var codec = AudioCodecResolver.ResolveActiveCodec(96, sampleRate: 8000, codecName: "", Empty);

        Assert.Equal(ActiveCodec.Pcmu, codec);
    }

    [Fact]
    public void Explicit_codec_name_wins_over_payload_type()
    {
        // PT 0 would statically resolve to PCMU, but the explicit name takes precedence.
        var codec = AudioCodecResolver.ResolveActiveCodec(0, sampleRate: 8000, codecName: "G722", Empty);

        Assert.Equal(ActiveCodec.G722, codec);
    }

    [Fact]
    public void Payload_type_codec_map_resolves_dynamic_payload_types()
    {
        var map = new Dictionary<int, string> { [96] = "OPUS" };

        var codec = AudioCodecResolver.ResolveActiveCodec(96, sampleRate: 8000, codecName: "", map);

        Assert.Equal(ActiveCodec.Opus, codec);
    }

    [Theory]
    [InlineData("G722", ActiveCodec.G722)]
    [InlineData("g.722", ActiveCodec.G722)]
    [InlineData("PCMA", ActiveCodec.Pcma)]
    [InlineData("a-law", ActiveCodec.Pcma)]
    [InlineData("A_LAW", ActiveCodec.Pcma)]
    [InlineData("PCMU", ActiveCodec.Pcmu)]
    [InlineData("mu-law", ActiveCodec.Pcmu)]
    [InlineData("MU_LAW", ActiveCodec.Pcmu)]
    [InlineData("opus", ActiveCodec.Opus)]
    [InlineData("  g722  ", ActiveCodec.G722)]
    public void Codec_names_map_case_and_separator_insensitively(string name, ActiveCodec expected)
    {
        Assert.Equal(expected, AudioCodecResolver.MapCodecNameToActiveCodec(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("speex")]
    public void Blank_or_unknown_codec_names_map_to_null(string? name)
    {
        Assert.Null(AudioCodecResolver.MapCodecNameToActiveCodec(name));
    }

    [Fact]
    public void Codec_sample_rates_match_the_per_device_table()
    {
        // 48 kHz Opus RTP clock rate (RFC 7587 §4.1), matching the pre-A8 OpusPayloadCodec constant.
        Assert.Equal(48_000, AudioCodecResolver.GetCodecSampleRate(ActiveCodec.Opus));
        Assert.Equal(16_000, AudioCodecResolver.GetCodecSampleRate(ActiveCodec.G722));
        Assert.Equal(8_000, AudioCodecResolver.GetCodecSampleRate(ActiveCodec.Pcmu));
        Assert.Equal(8_000, AudioCodecResolver.GetCodecSampleRate(ActiveCodec.Pcma));
    }

    private static ActiveCodec Parse(string name) => name switch
    {
        "PCMU" => ActiveCodec.Pcmu,
        "PCMA" => ActiveCodec.Pcma,
        "G722" => ActiveCodec.G722,
        "OPUS" => ActiveCodec.Opus,
        _ => throw new System.ArgumentOutOfRangeException(nameof(name), name, null)
    };
}
