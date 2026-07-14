using System.Net;
using CalloraVoipSdk.Core.Application.Ports.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sdp;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Video SDP negotiation (WebRTC phase 2b slice 1, RFC 3264/6184/7741): m=video offer
/// generation, video answers with codec matching, the zero-port decline for anything
/// unanswerable — and the RFC 3264 §6 guarantee that every offered m-line is answered.
/// </summary>
public sealed class VideoSdpNegotiationTests
{
    private static readonly IPEndPoint LocalAudio = new(IPAddress.Loopback, 41000);

    private static SdpVideoNegotiationOptions VideoOptions(params string[] codecs) => new()
    {
        Port = 41002,
        PreferredCodecNames = codecs.Length > 0 ? codecs : null,
    };

    // ── Offer generation ────────────────────────────────────────────────────────

    [Fact]
    public void Offer_with_video_options_carries_video_mline_and_h264_fmtp()
    {
        var offer = SdpUtilities.BuildDefaultSdp(LocalAudio, hold: false,
            new SdpMediaNegotiationOptions { Video = VideoOptions() });

        Assert.Contains("m=video 41002 RTP/AVP 96 97", offer, StringComparison.Ordinal);
        Assert.Contains("a=rtpmap:96 VP8/90000", offer, StringComparison.Ordinal);
        Assert.Contains("a=rtpmap:97 H264/90000", offer, StringComparison.Ordinal);
        Assert.Contains("a=fmtp:97 packetization-mode=1", offer, StringComparison.Ordinal);
        Assert.True(offer.IndexOf("m=audio", StringComparison.Ordinal)
                    < offer.IndexOf("m=video", StringComparison.Ordinal));
    }

    [Fact]
    public void Sdes_offer_stays_audio_only_despite_video_options()
    {
        // Per-m-line SDES video keys are not wired yet — fail closed, no video m-line.
        var offer = SdpUtilities.BuildDefaultSdp(LocalAudio, hold: false,
            new SdpMediaNegotiationOptions { Video = VideoOptions(), OfferSrtpCrypto = true });

        Assert.Contains("a=crypto:", offer, StringComparison.Ordinal);
        Assert.DoesNotContain("m=video", offer, StringComparison.Ordinal);
    }

    [Fact]
    public void Offer_without_video_options_stays_audio_only()
    {
        var offer = SdpUtilities.BuildDefaultSdp(LocalAudio, hold: false, new SdpMediaNegotiationOptions());
        Assert.DoesNotContain("m=video", offer, StringComparison.Ordinal);
    }

    // ── Answer: negotiation and RFC 3264 mirroring ──────────────────────────────

    [Fact]
    public void Video_offer_is_answered_with_matching_codec_and_carried_fmtp()
    {
        var answer = SdpUtilities.TryBuildNegotiatedAnswer(
            AudioVideoOffer(videoLine: "m=video 5004 RTP/AVP 100 101\r\n"
                + "a=rtpmap:100 H264/90000\r\n"
                + "a=fmtp:100 packetization-mode=1;profile-level-id=42e01f\r\n"
                + "a=rtpmap:101 VP9/90000\r\n"),
            LocalAudio, hold: false,
            new SdpMediaNegotiationOptions { Video = VideoOptions() });

        Assert.NotNull(answer);
        Assert.Contains("m=video 41002 RTP/AVP 100", answer!, StringComparison.Ordinal);
        Assert.Contains("a=rtpmap:100 H264/90000", answer, StringComparison.Ordinal);
        Assert.Contains("a=fmtp:100 packetization-mode=1;profile-level-id=42e01f", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("VP9", answer, StringComparison.Ordinal); // unsupported codec dropped
    }

    [Fact]
    public void Video_offer_without_local_video_gets_zero_port_mirror()
    {
        // The RFC 3264 §6 conformance fix: previously the video m-line vanished from
        // the answer entirely.
        var answer = SdpUtilities.TryBuildNegotiatedAnswer(
            AudioVideoOffer(), LocalAudio, hold: false, localOptions: null);

        Assert.NotNull(answer);
        Assert.Contains("m=audio 41000", answer!, StringComparison.Ordinal);
        Assert.Contains("m=video 0 RTP/AVP", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void Video_codec_mismatch_gets_zero_port_mirror()
    {
        var answer = SdpUtilities.TryBuildNegotiatedAnswer(
            AudioVideoOffer(videoLine: "m=video 5004 RTP/AVP 101\r\na=rtpmap:101 VP9/90000\r\n"),
            LocalAudio, hold: false,
            new SdpMediaNegotiationOptions { Video = VideoOptions() });

        Assert.NotNull(answer);
        Assert.Contains("m=video 0 RTP/AVP", answer!, StringComparison.Ordinal);
    }

    [Fact]
    public void Video_before_audio_keeps_answer_mline_order()
    {
        var offer =
            "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=peer\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\n"
            + "m=video 5004 RTP/AVP 96\r\na=rtpmap:96 VP8/90000\r\n"
            + "m=audio 5002 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\na=sendrecv\r\n";

        var answer = SdpUtilities.TryBuildNegotiatedAnswer(
            offer, LocalAudio, hold: false,
            new SdpMediaNegotiationOptions { Video = VideoOptions() });

        Assert.NotNull(answer);
        Assert.True(answer!.IndexOf("m=video", StringComparison.Ordinal)
                    < answer.IndexOf("m=audio", StringComparison.Ordinal));
        Assert.Contains("m=video 41002", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void Dtls_video_offer_is_answered_with_fingerprint_and_setup()
    {
        var identity = new SdpDtlsNegotiationOptions
        {
            FingerprintAlgorithm = "sha-256",
            FingerprintValue = string.Join(':', Enumerable.Repeat("AB", 32)),
        };
        var offer =
            "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=peer\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\n"
            + $"a=fingerprint:sha-256 {string.Join(':', Enumerable.Repeat("CD", 32))}\r\n"
            + "a=setup:actpass\r\n"
            + "m=audio 5002 UDP/TLS/RTP/SAVPF 0\r\na=rtpmap:0 PCMU/8000\r\n"
            + "m=video 5004 UDP/TLS/RTP/SAVPF 96\r\na=rtpmap:96 VP8/90000\r\n";

        var answer = SdpUtilities.TryBuildNegotiatedAnswer(
            offer, LocalAudio, hold: false,
            new SdpMediaNegotiationOptions { Video = VideoOptions(), Dtls = identity });

        Assert.NotNull(answer);
        Assert.Contains("m=video 41002 UDP/TLS/RTP/SAVPF 96", answer!, StringComparison.Ordinal);
        var videoSection = answer[answer.IndexOf("m=video", StringComparison.Ordinal)..];
        Assert.Contains($"a=fingerprint:sha-256 {identity.FingerprintValue}", videoSection, StringComparison.Ordinal);
        Assert.Contains("a=setup:active", videoSection, StringComparison.Ordinal);
    }

    [Fact]
    public void Sdes_video_offer_gets_zero_port_mirror_while_audio_negotiates_sdes()
    {
        var inlineKey = Convert.ToBase64String(new byte[30]);
        var offer =
            "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=peer\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\n"
            + "m=audio 5002 RTP/SAVP 0\r\na=rtpmap:0 PCMU/8000\r\n"
            + $"a=crypto:1 AES_CM_128_HMAC_SHA1_80 inline:{inlineKey}\r\n"
            + "m=video 5004 RTP/SAVP 96\r\na=rtpmap:96 VP8/90000\r\n"
            + $"a=crypto:1 AES_CM_128_HMAC_SHA1_80 inline:{inlineKey}\r\n";

        var answer = SdpUtilities.TryBuildNegotiatedAnswer(
            offer, LocalAudio, hold: false,
            new SdpMediaNegotiationOptions { Video = VideoOptions() });

        Assert.NotNull(answer);
        Assert.Contains("m=audio 41000 RTP/SAVP", answer!, StringComparison.Ordinal);
        Assert.Contains("m=video 0 RTP/SAVP", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void Unsupported_codec_on_colliding_dynamic_pt_gets_zero_port_mirror()
    {
        // VP9 offered on PT 96 — the same PT our local VP8 default uses. A bare PT
        // fallback would answer VP8 for a codec the peer never offered.
        var answer = SdpUtilities.TryBuildNegotiatedAnswer(
            AudioVideoOffer(videoLine: "m=video 5004 RTP/AVP 96\r\na=rtpmap:96 VP9/90000\r\n"),
            LocalAudio, hold: false,
            new SdpMediaNegotiationOptions { Video = VideoOptions() });

        Assert.NotNull(answer);
        Assert.Contains("m=video 0 RTP/AVP", answer!, StringComparison.Ordinal);
        Assert.DoesNotContain("VP8", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void Second_video_mline_gets_zero_port_mirror()
    {
        // Camera + screenshare: both cannot share the single local video port.
        var answer = SdpUtilities.TryBuildNegotiatedAnswer(
            AudioVideoOffer() + "m=video 5006 RTP/AVP 96\r\na=rtpmap:96 VP8/90000\r\n",
            LocalAudio, hold: false,
            new SdpMediaNegotiationOptions { Video = VideoOptions() });

        Assert.NotNull(answer);
        Assert.Contains("m=video 41002 RTP/AVP 96", answer!, StringComparison.Ordinal);
        Assert.Contains("m=video 0 RTP/AVP", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void H264_without_packetization_mode_1_is_not_matched()
    {
        // Absent packetization-mode means mode 0 (RFC 6184 §8.1) — our packetiser
        // always fragments large NALs as FU-A, which a mode-0 peer cannot receive.
        var answer = SdpUtilities.TryBuildNegotiatedAnswer(
            AudioVideoOffer(videoLine: "m=video 5004 RTP/AVP 100\r\na=rtpmap:100 H264/90000\r\n"),
            LocalAudio, hold: false,
            new SdpMediaNegotiationOptions { Video = VideoOptions() });

        Assert.NotNull(answer);
        Assert.Contains("m=video 0 RTP/AVP", answer!, StringComparison.Ordinal);
    }

    // ── Media parameters ────────────────────────────────────────────────────────

    [Fact]
    public void Media_parameters_carry_video_when_enabled_and_matched()
    {
        var parameters = SdpUtilities.TryParseMediaParameters(
            AudioVideoOffer(), LocalAudio,
            new SdpMediaNegotiationOptions { Video = VideoOptions() });

        Assert.NotNull(parameters?.Video);
        Assert.Equal(96, parameters!.Video!.PayloadType);
        Assert.Equal("VP8", parameters.Video.CodecName);
        Assert.Equal(90000, parameters.Video.ClockRate);
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, 5004), parameters.Video.RemoteEndPoint);
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, 41002), parameters.Video.LocalEndPoint);
    }

    [Fact]
    public void Media_parameters_stay_audio_only_without_video_options()
    {
        var parameters = SdpUtilities.TryParseMediaParameters(AudioVideoOffer(), LocalAudio, null);

        Assert.NotNull(parameters);
        Assert.Null(parameters!.Video);
    }

    [Fact]
    public void Media_parameters_omit_video_for_sdes_keyed_video_mline()
    {
        // Mirrors the negotiator's zero-port decline: never start media the answer refused.
        var inlineKey = Convert.ToBase64String(new byte[30]);
        var sdp =
            "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=peer\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\n"
            + "m=audio 5002 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\n"
            + "m=video 5004 RTP/SAVP 96\r\na=rtpmap:96 VP8/90000\r\n"
            + $"a=crypto:1 AES_CM_128_HMAC_SHA1_80 inline:{inlineKey}\r\n";

        var parameters = SdpUtilities.TryParseMediaParameters(
            sdp, LocalAudio, new SdpMediaNegotiationOptions { Video = VideoOptions() });

        Assert.NotNull(parameters);
        Assert.Null(parameters!.Video);
    }

    [Fact]
    public void Media_parameters_omit_dtls_video_without_local_identity()
    {
        var sdp =
            "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=peer\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\n"
            + $"a=fingerprint:sha-256 {string.Join(':', Enumerable.Repeat("CD", 32))}\r\n"
            + "m=audio 5002 UDP/TLS/RTP/SAVPF 0\r\na=rtpmap:0 PCMU/8000\r\n"
            + "m=video 5004 UDP/TLS/RTP/SAVPF 96\r\na=rtpmap:96 VP8/90000\r\n";

        var parameters = SdpUtilities.TryParseMediaParameters(
            sdp, LocalAudio, new SdpMediaNegotiationOptions { Video = VideoOptions() });

        Assert.NotNull(parameters);
        Assert.Null(parameters!.Video);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string AudioVideoOffer(string? videoLine = null) =>
        "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=peer\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\n"
        + "m=audio 5002 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\na=sendrecv\r\n"
        + (videoLine ?? "m=video 5004 RTP/AVP 96\r\na=rtpmap:96 VP8/90000\r\n");
}
