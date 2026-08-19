using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Multi-track BUNDLE <em>audio</em> at the SDP negotiation layer (4.7.0, RFC 8843 / RFC 8829 / RFC 3264 §6):
/// an SFU that forwards N participant audios offers/answers N <c>m=audio</c> lines over one shared BUNDLE
/// transport, each with its own numeric <c>a=mid</c>, its own <c>a=msid</c>, and per-m-line keying. The
/// generic multi-track offer path (P2a-i) already emits N audio m-lines; the answer path (P2a-ii) already
/// answers every audio m-line under BUNDLE. This slice closes the one audio-specific answer gap — a distinct
/// <c>a=msid</c> per audio m-line (<see cref="SdpMediaOptions.AudioMsidByMid"/>), since the pre-slice answer
/// stamped the single <see cref="SdpMediaOptions.AudioMsid"/> on every audio m-line. Audio never zero-ports
/// on the send side and carries no RTX/simulcast; the first audio anchors the transport. Transport/session/
/// API wiring for N-audio is a later slice — this covers the SDP model only.
/// </summary>
public sealed class SdpMultiTrackAudioTests
{
    private static readonly IPEndPoint Offerer = new(IPAddress.Loopback, 5000);
    private static readonly IPEndPoint Answerer = new(IPAddress.Loopback, 6000);

    // A realistic audio format set: one real codec plus telephone-event (RFC 4733) per m-line.
    private static readonly IReadOnlyList<SdpCodecDefinition> AudioCodecs =
    [
        new SdpCodecDefinition { PayloadType = 0, Name = "PCMU", ClockRate = 8000 },
        new SdpCodecDefinition { PayloadType = 101, Name = "telephone-event", ClockRate = 8000 },
    ];

    private static readonly IReadOnlyList<SdpCodecDefinition> VideoCodecs =
        [new SdpCodecDefinition { PayloadType = 96, Name = "VP8", ClockRate = 90000 }];

    private static SdpDtlsParameters Dtls(string tag) => new()
    {
        Algorithm = "sha-256",
        Fingerprint = string.Join(':', Enumerable.Repeat(tag, 32)),
    };

    private static readonly SdpIceParameters OffererIce = new() { Ufrag = "offufrag", Pwd = "offerer-password-22ch+" };
    private static readonly SdpIceParameters AnswererIce = new() { Ufrag = "ansufrag", Pwd = "answerer-password-22ch+" };

    private static SdpTrackOptions Audio(string? sid = null) => new()
    {
        Kind = "audio",
        Codecs = AudioCodecs,
        Msid = sid is null ? null : new SdpMsid { StreamId = sid, TrackId = sid + "-a" },
    };

    private static SdpTrackOptions Video(string? sid = null) => new()
    {
        Kind = "video",
        Codecs = VideoCodecs,
        Msid = sid is null ? null : new SdpMsid { StreamId = sid, TrackId = sid + "-v" },
    };

    private static SdpSessionDescription Offer(params SdpTrackOptions[] tracks) =>
        new SdpOfferAnswerNegotiator().CreateOffer(
            Offerer, AudioCodecs, SdpMediaDirection.SendRecv,
            new SdpMediaOptions { Bundle = true, RtcpMux = true, Dtls = Dtls("AA"), Ice = OffererIce, Tracks = tracks });

    private static SdpOfferAnswerResult Answer(SdpSessionDescription offer, SdpMediaOptions options) =>
        new SdpOfferAnswerNegotiator().NegotiateAnswer(
            offer, Answerer, AudioCodecs, SdpMediaDirection.SendRecv, options);

    // ---------------------------------------------------------------------
    // Offer: N audio m-lines
    // ---------------------------------------------------------------------

    [Fact]
    public void Three_audio_tracks_offer_three_audio_m_lines_with_ascending_numeric_mids_under_bundle()
    {
        var offer = Offer(Audio("p1"), Audio("p2"), Audio("p3"));

        Assert.Equal(3, offer.Media.Count);
        Assert.All(offer.Media, m => Assert.Equal("audio", m.MediaType));
        Assert.Equal(["0", "1", "2"], offer.Media.Select(m => m.Mid!).ToArray());
        Assert.Equal("BUNDLE 0 1 2", offer.Group);
        // One shared BUNDLE transport port on every audio m-line (RFC 8843).
        Assert.All(offer.Media, m => Assert.Equal(Offerer.Port, m.Port));
    }

    [Fact]
    public void Each_offered_audio_m_line_carries_its_own_msid_and_its_own_telephone_event_fmtp()
    {
        var offer = Offer(Audio("p1"), Audio("p2"));

        Assert.Equal("p1", offer.Media[0].Msid!.StreamId);
        Assert.Equal("p1-a", offer.Media[0].Msid!.TrackId);
        Assert.Equal("p2", offer.Media[1].Msid!.StreamId);
        Assert.Equal("p2-a", offer.Media[1].Msid!.TrackId);

        // telephone-event (RFC 4733) is emitted per audio m-line, independently — no line loses DTMF.
        Assert.All(offer.Media, m =>
        {
            Assert.Contains(m.Codecs, c => c.Name.Equals("telephone-event", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(m.Fmtp, f => f.PayloadType == 101 && f.Parameters == "0-16");
        });
    }

    [Fact]
    public void Mixed_two_audio_plus_one_video_offer_keeps_audio_before_video_by_track_order()
    {
        var offer = Offer(Audio("p1"), Audio("p2"), Video("cam"));

        Assert.Equal(["audio", "audio", "video"], offer.Media.Select(m => m.MediaType).ToArray());
        Assert.Equal(["0", "1", "2"], offer.Media.Select(m => m.Mid!).ToArray());
        Assert.Equal("BUNDLE 0 1 2", offer.Group);
    }

    // ---------------------------------------------------------------------
    // Answer: N active audio m-lines, per-MID msid
    // ---------------------------------------------------------------------

    [Fact]
    public void A_three_audio_offer_is_answered_with_three_active_audio_m_lines_mid_one_to_one()
    {
        var result = Answer(Offer(Audio(), Audio(), Audio()), new SdpMediaOptions
        {
            RtcpMux = true,
            Dtls = Dtls("BB"),
            Ice = AnswererIce,
        });

        Assert.True(result.Success);
        var media = result.Answer!.Media;
        Assert.Equal(3, media.Count);
        Assert.All(media, m => Assert.Equal("audio", m.MediaType));
        Assert.Equal(["0", "1", "2"], media.Select(m => m.Mid!).ToArray());     // mids mirrored 1:1 (RFC 8829)
        Assert.Equal("BUNDLE 0 1 2", result.Answer.Group);                     // all mids accepted
        // Audio anchors the transport: every audio m-line is active (non-zero port), none declined.
        Assert.All(media, m => Assert.True(m.Port > 0, $"audio m-line {m.Mid} was declined"));
    }

    [Fact]
    public void Every_answered_audio_m_line_is_dtls_keyed_with_the_local_fingerprint()
    {
        var result = Answer(Offer(Audio(), Audio(), Audio()), new SdpMediaOptions
        {
            RtcpMux = true,
            Dtls = Dtls("BB"),
        });

        var expected = string.Join(':', Enumerable.Repeat("BB", 32));
        Assert.All(result.Answer!.Media, m =>
        {
            Assert.NotNull(m.Fingerprint);
            Assert.Equal(expected, m.Fingerprint!.Value);
        });
    }

    [Fact]
    public void The_answer_names_a_distinct_msid_per_audio_m_line_via_audio_msid_by_mid()
    {
        // An SFU forwarding two participants gives each answered audio m-line its own a=msid, keyed by MID.
        var byMid = new Dictionary<string, SdpMsid>
        {
            ["0"] = new SdpMsid { StreamId = "alice", TrackId = "alice-audio" },
            ["1"] = new SdpMsid { StreamId = "bob", TrackId = "bob-audio" },
        };

        var result = Answer(Offer(Audio(), Audio()), new SdpMediaOptions
        {
            RtcpMux = true,
            Dtls = Dtls("BB"),
            AudioMsidByMid = byMid,
        });

        var media = result.Answer!.Media;
        Assert.Equal("alice", media[0].Msid!.StreamId);
        Assert.Equal("alice-audio", media[0].Msid!.TrackId);
        Assert.Equal("bob", media[1].Msid!.StreamId);
        Assert.Equal("bob-audio", media[1].Msid!.TrackId);
    }

    [Fact]
    public void A_mid_absent_from_the_per_mid_map_falls_back_to_the_single_audio_msid()
    {
        // Only mid 0 is named per-line; mid 1 falls back to the session-wide AudioMsid.
        var result = Answer(Offer(Audio(), Audio()), new SdpMediaOptions
        {
            RtcpMux = true,
            Dtls = Dtls("BB"),
            AudioMsid = new SdpMsid { StreamId = "fallback", TrackId = "fallback-a" },
            AudioMsidByMid = new Dictionary<string, SdpMsid>
            {
                ["0"] = new SdpMsid { StreamId = "named", TrackId = "named-a" },
            },
        });

        var media = result.Answer!.Media;
        Assert.Equal("named", media[0].Msid!.StreamId);
        Assert.Equal("fallback", media[1].Msid!.StreamId);
    }

    [Fact]
    public void The_mid_extension_is_echoed_with_one_shared_id_on_every_answered_audio_m_line()
    {
        var result = Answer(Offer(Audio(), Audio(), Audio()), new SdpMediaOptions
        {
            RtcpMux = true,
            Dtls = Dtls("BB"),
        });

        var midIds = result.Answer!.Media
            .Select(m => m.Extensions.Single(e => e.Uri == RtpHeaderExtensionUris.Mid).Id)
            .Distinct()
            .ToArray();
        Assert.Equal([1], midIds); // the offered id, echoed on every m-line (RFC 8843 §9)
    }

    // ---------------------------------------------------------------------
    // Plain RTP N-audio + roundtrip
    // ---------------------------------------------------------------------

    [Fact]
    public void Plain_rtp_two_audio_offer_and_answer_stay_on_rtp_avp_and_round_trip()
    {
        // No DTLS, no SDES: plain RTP/AVP, two audio m-lines. Audio must not be forced secure or dropped.
        var offer = new SdpOfferAnswerNegotiator().CreateOffer(
            Offerer, AudioCodecs, SdpMediaDirection.SendRecv,
            new SdpMediaOptions { Bundle = true, RtcpMux = true, Tracks = [Audio("p1"), Audio("p2")] });

        Assert.All(offer.Media, m => Assert.Equal("RTP/AVP", m.Profile));

        var result = new SdpOfferAnswerNegotiator().NegotiateAnswer(
            offer, Answerer, AudioCodecs, SdpMediaDirection.SendRecv,
            new SdpMediaOptions { RtcpMux = true });

        Assert.True(result.Success);
        var media = result.Answer!.Media;
        Assert.Equal(2, media.Count);
        Assert.All(media, m =>
        {
            Assert.Equal("audio", m.MediaType);
            Assert.Equal("RTP/AVP", m.Profile);
            Assert.True(m.Port > 0);
        });

        // Serialize→parse roundtrip is stable: mids, media types, and BUNDLE group survive.
        var sdp = new SdpSessionSerializer().Serialize(result.Answer!);
        var reparsed = new SdpSessionParser().Parse(sdp);
        Assert.Equal(["0", "1"], reparsed.Media.Select(m => m.Mid!).ToArray());
        Assert.Equal(["audio", "audio"], reparsed.Media.Select(m => m.MediaType).ToArray());
        Assert.Equal("BUNDLE 0 1", reparsed.Group);
    }

    [Fact]
    public void A_multi_audio_bundle_answer_round_trips_through_serialize_and_parse()
    {
        var result = Answer(Offer(Audio(), Audio(), Audio()), new SdpMediaOptions
        {
            RtcpMux = true,
            Dtls = Dtls("BB"),
            Ice = AnswererIce,
        });

        var sdp = new SdpSessionSerializer().Serialize(result.Answer!);
        var reparsed = new SdpSessionParser().Parse(sdp);

        Assert.Equal(["0", "1", "2"], reparsed.Media.Select(m => m.Mid!).ToArray());
        Assert.Equal(["audio", "audio", "audio"], reparsed.Media.Select(m => m.MediaType).ToArray());
        Assert.Equal("BUNDLE 0 1 2", reparsed.Group);
    }

    // ---------------------------------------------------------------------
    // Regression: single-audio answer with only AudioMsid is unchanged
    // ---------------------------------------------------------------------

    [Fact]
    public void Single_audio_answer_with_only_audio_msid_is_unchanged_by_the_per_mid_map()
    {
        // A one-audio offer answered with only AudioMsid (no AudioMsidByMid) is byte-identical to the
        // pre-slice behavior — the map defaults to null and the single AudioMsid is stamped as before.
        var offer = new SdpOfferAnswerNegotiator().CreateOffer(
            Offerer, AudioCodecs, SdpMediaDirection.SendRecv,
            new SdpMediaOptions { Bundle = true, RtcpMux = true, Dtls = Dtls("AA"), Ice = OffererIce });

        var result = Answer(offer, new SdpMediaOptions
        {
            RtcpMux = true,
            Dtls = Dtls("BB"),
            Ice = AnswererIce,
            AudioMsid = new SdpMsid { StreamId = "only", TrackId = "only-a" },
        });

        var media = Assert.Single(result.Answer!.Media);
        Assert.Equal("audio", media.MediaType);
        Assert.Equal("audio", media.Mid);              // historic semantic mid on the fixed 1+1 path
        Assert.Equal("only", media.Msid!.StreamId);
        Assert.Equal("only-a", media.Msid!.TrackId);
    }
}
