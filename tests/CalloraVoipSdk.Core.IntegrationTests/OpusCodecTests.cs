using System.Net;
using CalloraVoipSdk.Core.Application.Media.Sessions;
using CalloraVoipSdk.Core.Application.Ports.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sdp;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Opus codec support (package B.2/O1, RFC 7587): managed Concentus encode/decode,
/// SDP negotiation with a mirrored dynamic payload type when Opus is explicitly
/// preferred, correct 48 kHz RTP parameters — and no change to the default codec
/// set when Opus is not requested (the live agent pins PCMU).
/// </summary>
public sealed class OpusCodecTests
{
    /// <summary>The sipgate SIPconnect offer pattern from the live trunk (PT 107 Opus first).</summary>
    private static string SipgateOffer(int port = 20000) =>
        "v=0\r\n"
        + "o=- 1 1 IN IP4 127.0.0.1\r\n"
        + "s=sGW\r\n"
        + "c=IN IP4 127.0.0.1\r\n"
        + "t=0 0\r\n"
        + $"m=audio {port} RTP/AVP 107 9 8 0 3 101 113\r\n"
        + "a=rtpmap:107 opus/48000/2\r\n"
        + "a=fmtp:107 useinbandfec=1\r\n"
        + "a=rtpmap:9 G722/8000\r\n"
        + "a=rtpmap:8 PCMA/8000\r\n"
        + "a=rtpmap:0 PCMU/8000\r\n"
        + "a=rtpmap:3 GSM/8000\r\n"
        + "a=rtpmap:101 telephone-event/48000\r\n"
        + "a=fmtp:101 0-16\r\n"
        + "a=rtpmap:113 telephone-event/8000\r\n"
        + "a=fmtp:113 0-16\r\n"
        + "a=sendrecv\r\n"
        + "a=ptime:20\r\n";

    private static readonly IPEndPoint LocalEndPoint = new(IPAddress.Loopback, 40000);

    private static byte[] SinePcm48k(int samples)
    {
        var pcm = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var value = (short)(Math.Sin(2 * Math.PI * 440 * i / 48000.0) * 12000);
            pcm[i * 2] = (byte)value;
            pcm[i * 2 + 1] = (byte)(value >> 8);
        }
        return pcm;
    }

    // ── Codec roundtrip ─────────────────────────────────────────────────────────

    [Fact]
    public void Encode_decode_roundtrip_preserves_frame_size_and_energy()
    {
        var codec = new OpusPayloadCodec();
        var pcm = SinePcm48k(OpusPayloadCodec.SamplesPerDefaultFrame);

        var encoded = codec.Encode(pcm);
        Assert.InRange(encoded.Length, 1, pcm.Length / 4); // compressed, plausible VoIP size

        var decoded = codec.Decode(encoded);
        Assert.Equal(pcm.Length, decoded.Length); // 20 ms in → 20 ms out

        // The decoded signal carries energy (not silence) — coarse sanity, not fidelity.
        var energy = 0L;
        for (var i = 0; i < decoded.Length; i += 2)
            energy += Math.Abs((short)(decoded[i] | (decoded[i + 1] << 8)));
        Assert.True(energy / (decoded.Length / 2) > 500, "decoded signal is near-silent");
    }

    [Fact]
    public void Encoder_state_carries_across_frames()
    {
        var codec = new OpusPayloadCodec();
        for (var frame = 0; frame < 5; frame++)
        {
            var encoded = codec.Encode(SinePcm48k(OpusPayloadCodec.SamplesPerDefaultFrame));
            Assert.Equal(
                OpusPayloadCodec.SamplesPerDefaultFrame * 2,
                codec.Decode(encoded).Length);
        }
    }

    // ── Transcoder integration ──────────────────────────────────────────────────

    [Fact]
    public void Transcoder_resolves_opus_by_name_and_roundtrips_pcm()
    {
        Assert.Equal(PayloadCodecKind.Opus, AudioPayloadTranscoder.ResolveCodecKind("opus", 107));

        Assert.True(AudioPayloadTranscoder.TryCreatePcmFilePlanForCall(
            PayloadCodecKind.Opus,
            payloadType: 107,
            clockRate: 48000,
            samplesPerFrame: 960,
            codecName: "opus",
            out var plan,
            out var error), error);
        Assert.NotNull(plan);
        Assert.Equal(48000, plan!.CodecContext.SampleRate);

        // fromFileFrame encodes PCM16 → Opus; toFileFrame decodes back — full plan roundtrip.
        var pcm = SinePcm48k(960);
        var encoded = plan.FromFileFrame(new CalloraVoipSdk.Core.Application.Media.MediaFrame(pcm, 107, 960));
        Assert.InRange(encoded.Payload.Length, 1, pcm.Length / 4);
        var decoded = plan.ToFileFrame(encoded);
        Assert.Equal(pcm.Length, decoded.Payload.Length);
    }

    // ── SDP negotiation ─────────────────────────────────────────────────────────

    [Fact]
    public void Preferred_opus_is_negotiated_with_mirrored_dynamic_pt()
    {
        var answer = SdpUtilities.TryBuildNegotiatedAnswer(
            SipgateOffer(),
            LocalEndPoint,
            hold: false,
            new SdpMediaNegotiationOptions { PreferredCodecNames = ["opus"] });

        Assert.NotNull(answer);
        Assert.Contains("a=rtpmap:107 opus/48000/2", answer);
        Assert.Contains("m=audio 40000 RTP/AVP 107", answer);
        // The offer's Opus fmtp must be carried into the answer (RFC 3264 §6.1).
        Assert.Contains("a=fmtp:107 useinbandfec=1", answer);
    }

    [Fact]
    public void Opus_media_parameters_use_the_48k_rtp_clock()
    {
        var parameters = SdpUtilities.TryParseMediaParameters(
            SipgateOffer(),
            LocalEndPoint,
            new SdpMediaNegotiationOptions { PreferredCodecNames = ["opus"] });

        Assert.NotNull(parameters);
        Assert.Equal(107, parameters!.PayloadType);
        Assert.Equal(48000, parameters.ClockRate);
        Assert.Equal(960, parameters.SamplesPerPacket); // 20 ms at 48 kHz
        Assert.Equal("opus", parameters.CodecName, ignoreCase: true);
        // RFC 4733 §2.1: the event stream shares the audio clock — 48 kHz line (PT 101).
        Assert.Equal(101, parameters.TelephoneEventPayloadType);
    }

    // ── Regression: without explicit preference nothing changes ────────────────

    [Fact]
    public void Without_preference_the_default_answer_stays_opus_free()
    {
        var answer = SdpUtilities.TryBuildNegotiatedAnswer(
            SipgateOffer(),
            LocalEndPoint,
            hold: false,
            localOptions: null);

        Assert.NotNull(answer);
        Assert.DoesNotContain("opus", answer, StringComparison.OrdinalIgnoreCase);
        // The live agent path: G722 preferred by default set order.
        Assert.Contains("a=rtpmap:9 G722/8000", answer);
    }

    [Fact]
    public void Pcmu_preference_still_yields_pcmu()
    {
        // The live agent pins PCMU — that path must be byte-compatible with 3.2.0.
        var parameters = SdpUtilities.TryParseMediaParameters(
            SipgateOffer(),
            LocalEndPoint,
            new SdpMediaNegotiationOptions { PreferredCodecNames = ["PCMU"] });

        Assert.NotNull(parameters);
        Assert.Equal(0, parameters!.PayloadType);
        Assert.Equal(8000, parameters.ClockRate);
        Assert.Equal(160, parameters.SamplesPerPacket);
        // Clock-matched RFC 4733 event line for an 8 kHz codec (PT 113, not 101/48000).
        Assert.Equal(113, parameters.TelephoneEventPayloadType);
    }
}
