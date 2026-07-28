using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Multi-track BUNDLE answer negotiation (4.7.0 P2a-ii, RFC 3264 §6 / RFC 8843 / RFC 8829 §5.3.1): a
/// multi-track BUNDLE offer is answered with one m-line per offered m-line, in offer order, mid preserved
/// 1:1, every audio and video m-line keyed identically to the single-audio path. Without BUNDLE only the
/// first m-line of each media type is answered; a second same-type m-line is declined with a zero-port
/// mirror. The single-audio (+optional video) answer stays byte-identical (verified by the existing answer
/// tests). Offer input is built through the P2a-i multi-track offer path.
/// </summary>
public sealed class SdpMultiTrackAnswerTests
{
    private static readonly IPEndPoint Offerer = new(IPAddress.Loopback, 5000);
    private static readonly IPEndPoint Answerer = new(IPAddress.Loopback, 6000);

    private static readonly IReadOnlyList<SdpCodecDefinition> AudioCodecs =
        [new SdpCodecDefinition { PayloadType = 0, Name = "PCMU", ClockRate = 8000 }];

    private static readonly IReadOnlyList<SdpCodecDefinition> VideoCodecs =
        [new SdpCodecDefinition { PayloadType = 96, Name = "VP8", ClockRate = 90000 }];

    private static SdpDtlsParameters Dtls(string tag) => new()
    {
        Algorithm = "sha-256",
        Fingerprint = string.Join(':', Enumerable.Repeat(tag, 32)),
    };

    private static readonly SdpIceParameters OffererIce = new() { Ufrag = "offufrag", Pwd = "offerer-password-22ch+" };
    private static readonly SdpIceParameters AnswererIce = new() { Ufrag = "ansufrag", Pwd = "answerer-password-22ch+" };

    private static SdpTrackOptions Audio() => new() { Kind = "audio", Codecs = AudioCodecs };
    private static SdpTrackOptions Video() => new() { Kind = "video", Codecs = VideoCodecs };

    private static SdpSessionDescription Offer(bool bundle, params SdpTrackOptions[] tracks) =>
        new SdpOfferAnswerNegotiator().CreateOffer(
            Offerer, AudioCodecs, SdpMediaDirection.SendRecv,
            new SdpMediaOptions { Bundle = bundle, RtcpMux = true, Dtls = Dtls("AA"), Ice = OffererIce, Tracks = tracks });

    private static SdpOfferAnswerResult Answer(SdpSessionDescription offer) =>
        new SdpOfferAnswerNegotiator().NegotiateAnswer(
            offer, Answerer, AudioCodecs, SdpMediaDirection.SendRecv,
            new SdpMediaOptions
            {
                RtcpMux = true,
                Dtls = Dtls("BB"),
                Ice = AnswererIce,
                Video = new SdpVideoMediaOptions { Port = Answerer.Port, Codecs = VideoCodecs },
            });

    [Fact]
    public void A_bundle_offer_with_two_audio_and_two_video_is_answered_with_four_active_m_lines()
    {
        var result = Answer(Offer(bundle: true, Audio(), Audio(), Video(), Video()));

        Assert.True(result.Success);
        var media = result.Answer!.Media;
        Assert.Equal(4, media.Count);
        Assert.Equal(["audio", "audio", "video", "video"], media.Select(m => m.MediaType).ToArray());
        Assert.All(media, m => Assert.True(m.Port > 0, $"m-line {m.Mid} was declined (zero port)"));
    }

    [Fact]
    public void The_answer_preserves_offer_m_line_order_and_numeric_mids()
    {
        var result = Answer(Offer(bundle: true, Video(), Audio(), Video()));

        var media = result.Answer!.Media;
        Assert.Equal(["video", "audio", "video"], media.Select(m => m.MediaType).ToArray());
        Assert.Equal(["0", "1", "2"], media.Select(m => m.Mid).ToArray());   // mids mirrored 1:1 (RFC 8829)
        Assert.Equal("BUNDLE 0 1 2", result.Answer.Group);                    // group lists the accepted mids
    }

    [Fact]
    public void Every_answered_m_line_carries_the_local_dtls_fingerprint()
    {
        var result = Answer(Offer(bundle: true, Audio(), Video(), Video()));

        // Every m-line is DTLS-keyed with our own fingerprint (RFC 5763), like the single-audio path.
        Assert.All(result.Answer!.Media, m =>
        {
            Assert.NotNull(m.Fingerprint);
            Assert.Equal("BB:BB:BB:BB:BB:BB:BB:BB:BB:BB:BB:BB:BB:BB:BB:BB:BB:BB:BB:BB:BB:BB:BB:BB:BB:BB:BB:BB:BB:BB:BB:BB", m.Fingerprint!.Value);
        });
    }

    [Fact]
    public void The_mid_extension_is_echoed_with_one_id_on_every_answered_m_line()
    {
        var result = Answer(Offer(bundle: true, Audio(), Video(), Video()));

        var midIds = result.Answer!.Media
            .Select(m => m.Extensions.Single(e => e.Uri == RtpHeaderExtensionUris.Mid).Id)
            .Distinct()
            .ToArray();
        Assert.Equal([1], midIds); // the offered id, echoed on every m-line (RFC 8843 §9)
    }

    [Fact]
    public void Without_bundle_a_second_audio_m_line_is_declined_with_a_zero_port_mirror()
    {
        var result = Answer(Offer(bundle: false, Audio(), Audio()));

        Assert.True(result.Success);
        var media = result.Answer!.Media;
        Assert.Equal(2, media.Count);
        Assert.True(media[0].Port > 0);                  // first audio answered
        Assert.Equal(0, media[1].Port);                  // second audio declined (no BUNDLE, one local port)
    }

    [Fact]
    public void A_multi_track_bundle_answer_round_trips_through_serialize_and_parse()
    {
        var result = Answer(Offer(bundle: true, Audio(), Video(), Video()));
        var sdp = new SdpSessionSerializer().Serialize(result.Answer!);

        var reparsed = new SdpSessionParser().Parse(sdp);

        Assert.Equal(["0", "1", "2"], reparsed.Media.Select(m => m.Mid).ToArray());
        Assert.Equal(["audio", "video", "video"], reparsed.Media.Select(m => m.MediaType).ToArray());
        Assert.Equal("BUNDLE 0 1 2", reparsed.Group);
    }
}
