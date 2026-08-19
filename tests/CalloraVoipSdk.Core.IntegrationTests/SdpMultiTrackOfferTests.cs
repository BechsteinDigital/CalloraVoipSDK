using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Multi-track BUNDLE offer generation (4.7.0 P2a-i, RFC 8843 / RFC 8829): when the caller supplies an
/// explicit <c>Tracks</c> list, the negotiator emits one m-line per track with a numeric <c>a=mid</c> by
/// index (0, 1, 2, … — libwebrtc/SIPSorcery parity) and a <c>a=group:BUNDLE 0 1 …</c>, over one shared
/// transport port. Every m-line keeps the same per-m-line shape the fixed 1+1 path emits (msid, MID/RID
/// header extensions, per-m-line SDES, send-side simulcast). An empty/absent list falls back to the
/// byte-identical single-audio path (SIP and existing 1+1 WebRTC offers unchanged). Answer-side multi-track
/// negotiation is a separate slice (P2a-ii).
/// </summary>
public sealed class SdpMultiTrackOfferTests
{
    private static readonly IPEndPoint Local = new(IPAddress.Loopback, 5000);

    private static readonly IReadOnlyList<SdpCodecDefinition> AudioCodecs =
        [new SdpCodecDefinition { PayloadType = 0, Name = "PCMU", ClockRate = 8000 }];

    private static readonly IReadOnlyList<SdpCodecDefinition> VideoCodecs =
        [new SdpCodecDefinition { PayloadType = 96, Name = "H264", ClockRate = 90000 }];

    private static readonly SdpDtlsParameters Dtls = new()
    {
        Algorithm = "sha-256",
        Fingerprint = "AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99",
    };

    private static readonly SdpIceParameters Ice = new() { Ufrag = "ufrag1", Pwd = "password-at-least-22-chars" };

    private static SdpTrackOptions AudioTrack(string? streamId = null) => new()
    {
        Kind = "audio",
        Codecs = AudioCodecs,
        Msid = streamId is null ? null : new SdpMsid { StreamId = streamId, TrackId = streamId + "-a" },
    };

    private static SdpTrackOptions VideoTrack(string? streamId = null, IReadOnlyList<string>? rids = null) => new()
    {
        Kind = "video",
        Codecs = VideoCodecs,
        Msid = streamId is null ? null : new SdpMsid { StreamId = streamId, TrackId = streamId + "-v" },
        SimulcastSendRids = rids ?? [],
    };

    private static SdpSessionDescription Offer(params SdpTrackOptions[] tracks) =>
        new SdpOfferAnswerNegotiator().CreateOffer(
            Local, AudioCodecs, SdpMediaDirection.SendRecv,
            new SdpMediaOptions { Bundle = true, RtcpMux = true, Dtls = Dtls, Ice = Ice, Tracks = tracks });

    [Fact]
    public void Two_audio_and_two_video_tracks_emit_four_m_lines_with_numeric_mids_and_bundle_group()
    {
        var offer = Offer(AudioTrack(), AudioTrack(), VideoTrack(), VideoTrack());

        Assert.Equal(4, offer.Media.Count);
        Assert.Equal(["0", "1", "2", "3"], offer.Media.Select(m => m.Mid!).ToArray());
        Assert.Equal(["audio", "audio", "video", "video"], offer.Media.Select(m => m.MediaType).ToArray());
        Assert.Equal("BUNDLE 0 1 2 3", offer.Group);
        // One shared BUNDLE transport port on every m-line (RFC 8843).
        Assert.All(offer.Media, m => Assert.Equal(Local.Port, m.Port));
    }

    [Fact]
    public void M_line_order_follows_track_order()
    {
        var offer = Offer(VideoTrack(), AudioTrack(), VideoTrack());

        Assert.Equal(["video", "audio", "video"], offer.Media.Select(m => m.MediaType).ToArray());
        Assert.Equal(["0", "1", "2"], offer.Media.Select(m => m.Mid!).ToArray());
        Assert.Equal("BUNDLE 0 1 2", offer.Group);
    }

    [Fact]
    public void Each_track_carries_its_own_msid()
    {
        var offer = Offer(AudioTrack("streamA"), VideoTrack("streamB"));

        Assert.Equal("streamA", offer.Media[0].Msid!.StreamId);
        Assert.Equal("streamA-a", offer.Media[0].Msid!.TrackId);
        Assert.Equal("streamB", offer.Media[1].Msid!.StreamId);
        Assert.Equal("streamB-v", offer.Media[1].Msid!.TrackId);
    }

    [Fact]
    public void The_mid_header_extension_shares_one_id_across_every_m_line()
    {
        var offer = Offer(AudioTrack(), VideoTrack(), VideoTrack());

        var midIds = offer.Media
            .Select(m => m.Extensions.Single(e => e.Uri == RtpHeaderExtensionUris.Mid).Id)
            .Distinct()
            .ToArray();

        Assert.Equal([1], midIds); // MID offered first on every m-line → the same id 1 (RFC 8843 §9)
    }

    [Fact]
    public void A_video_track_with_rids_offers_send_side_simulcast()
    {
        var offer = Offer(AudioTrack(), VideoTrack(rids: ["hi", "lo"]));

        var video = offer.Media[1];
        Assert.Equal(["hi", "lo"], video.Rids.Select(r => r.Id).ToArray());
        Assert.NotNull(video.Simulcast);
        Assert.Equal(["hi", "lo"], video.Simulcast!.Send.ToArray());
        // RID header extension is offered alongside MID on the simulcast m-line (RFC 8852).
        Assert.Contains(video.Extensions, e => e.Uri == RtpHeaderExtensionUris.Rid);
    }

    [Fact]
    public void A_multi_track_offer_round_trips_through_serialize_and_parse()
    {
        var offer = Offer(AudioTrack("s"), VideoTrack("s"), VideoTrack("s"));
        var sdp = new SdpSessionSerializer().Serialize(offer);

        var reparsed = new SdpSessionParser().Parse(sdp);

        Assert.Equal(3, reparsed.Media.Count);
        Assert.Equal(["0", "1", "2"], reparsed.Media.Select(m => m.Mid!).ToArray());
        Assert.Equal(["audio", "video", "video"], reparsed.Media.Select(m => m.MediaType).ToArray());
        Assert.Equal("BUNDLE 0 1 2", reparsed.Group);
    }

    [Fact]
    public void An_empty_track_list_falls_back_to_the_fixed_single_audio_offer()
    {
        // Tracks = [] must be indistinguishable from not passing Tracks at all: the byte-identical 1+1 path
        // with the historic semantic mid, so the SIP path and existing WebRTC offers are unaffected.
        var offer = new SdpOfferAnswerNegotiator().CreateOffer(
            Local, AudioCodecs, SdpMediaDirection.SendRecv,
            new SdpMediaOptions { Bundle = true, RtcpMux = true, Dtls = Dtls, Ice = Ice, Tracks = [] });

        var media = Assert.Single(offer.Media);
        Assert.Equal("audio", media.MediaType);
        Assert.Equal("audio", media.Mid);            // historic semantic mid, not "0"
        Assert.Equal("BUNDLE audio", offer.Group);
    }
}
