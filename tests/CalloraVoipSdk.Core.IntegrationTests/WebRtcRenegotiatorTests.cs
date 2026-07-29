using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.WebRtc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The mid-call renegotiation diff (4.7.0 P3b-3), tested deterministically at the session level (no DTLS/ICE
/// timing): <see cref="WebRtcRenegotiator"/> diffs a re-offer's video m-lines against a running
/// <see cref="BundledMediaSession"/> and applies the delta live via <see cref="BundledMediaSession.AddVideoTrack"/>
/// / <see cref="BundledMediaSession.SetVideoTrackInactive"/>. SSRC allocation is seeded from the live session's
/// outbound SSRCs, so an added track's SSRC never collides a running one (RFC 3550 §8.1). An ICE restart (a
/// rotated remote ICE ufrag) is rejected — the transport is never rebuilt on this path.
/// </summary>
public sealed class WebRtcRenegotiatorTests
{
    private static readonly IReadOnlyList<SdpCodecDefinition> Pcmu =
        [new SdpCodecDefinition { PayloadType = 0, Name = "PCMU", ClockRate = 8000 }];
    private static readonly SdpCodecDefinition H264 = new() { PayloadType = 96, Name = "H264", ClockRate = 90000 };

    [Fact]
    public async Task Diff_adds_a_new_video_track_with_an_ssrc_distinct_from_the_live_session()
    {
        // Start with one video m-line (numeric MIDs: audio "0", video "1").
        var (offer, answer) = Exchange(videoMids: ["1"]);
        await using var session = BuildSession(offer, answer);
        Assert.Equal(["1"], session.VideoMids);
        var liveSsrcs = session.OutboundSsrcs;

        // Re-offer adds a SECOND video m-line (MID "2"); the answer accepts both.
        var (reOffer, reAnswer) = Exchange(videoMids: ["1", "2"]);
        var renegotiator = new WebRtcRenegotiator(NullLoggerFactory.Instance);

        var diff = renegotiator.ComputeDiff(session, reAnswer, reOffer);

        // The diff adds exactly MID "2" (MID "1" stays live, untouched) with a distinct SSRC, no removals.
        Assert.Empty(diff.MidsToDeactivate);
        var added = Assert.Single(diff.TracksToAdd);
        Assert.Equal("2", added.Mid);
        Assert.DoesNotContain(added.Ssrc, liveSsrcs); // RFC 3550 §8.1: distinct from every running SSRC

        // Applying the diff mutates the LIVE session: the new MID is now a video track, and its SSRC joins the pool.
        renegotiator.Apply(session, diff);
        Assert.Equal(["1", "2"], session.VideoMids);
        Assert.Contains(added.Ssrc, session.OutboundSsrcs);
    }

    [Fact]
    public async Task Diff_deactivates_a_video_track_the_re_offer_dropped()
    {
        // Start with two video m-lines ("1" and "2").
        var (offer, answer) = Exchange(videoMids: ["1", "2"]);
        await using var session = BuildSession(offer, answer);
        Assert.Equal(["1", "2"], session.VideoMids);

        // Re-offer keeps only "1" (drops "2"); the answer mirrors it.
        var (reOffer, reAnswer) = Exchange(videoMids: ["1"]);
        var renegotiator = new WebRtcRenegotiator(NullLoggerFactory.Instance);

        var diff = renegotiator.ComputeDiff(session, reAnswer, reOffer);

        // The diff deactivates exactly MID "2", adds nothing.
        Assert.Empty(diff.TracksToAdd);
        Assert.Equal(["2"], diff.MidsToDeactivate);

        renegotiator.Apply(session, diff);
        Assert.Equal(["1"], session.VideoMids); // the dropped track is gone from the live session
    }

    [Fact]
    public async Task An_unchanged_re_offer_is_an_empty_diff()
    {
        var (offer, answer) = Exchange(videoMids: ["1"]);
        await using var session = BuildSession(offer, answer);

        var (reOffer, reAnswer) = Exchange(videoMids: ["1"]);
        var diff = new WebRtcRenegotiator(NullLoggerFactory.Instance).ComputeDiff(session, reAnswer, reOffer);

        Assert.True(diff.IsEmpty);
    }

    [Fact]
    public async Task An_ice_restart_re_offer_is_rejected()
    {
        var (offer, answer) = Exchange(videoMids: ["1"]);
        await using var session = BuildSession(offer, answer);

        // Re-offer rotates the remote (offer) ICE ufrag → an ICE restart the track-diff path does not support.
        var (reOffer, reAnswer) = Exchange(videoMids: ["1"], offerUfrag: "restartedUfrag");
        var renegotiator = new WebRtcRenegotiator(NullLoggerFactory.Instance);

        var ex = Assert.Throws<InvalidOperationException>(() => renegotiator.ComputeDiff(session, reAnswer, reOffer));
        Assert.Contains("ICE restart", ex.Message, StringComparison.Ordinal);
    }

    // ── harness ──────────────────────────────────────────────────────────────────

    // Builds a running (not started) bundle session for the answerer from a negotiated exchange: localDescription
    // is the answer, remoteDescription the offer, so the session's remote ICE ufrag is the offer's ufrag.
    private static BundledMediaSession BuildSession(SdpSessionDescription offer, SdpSessionDescription answer)
    {
        var session = WebRtcSessionFactory.TryCreate(
            offer, answer, PeerOptions(), Handshaker(), DtlsCertificate.GenerateEcdsaP256(), NullLoggerFactory.Instance);
        Assert.NotNull(session);
        return session!;
    }

    // A BUNDLE offer/answer with an audio track (MID "0") plus one video m-line per entry in videoMids, using the
    // numeric-MID multi-track path so several video sections carry stable numeric MIDs. Both sides send-recv, so
    // every video m-line negotiates for sending (an outbound track is built for each).
    private static (SdpSessionDescription Offer, SdpSessionDescription Answer) Exchange(
        IReadOnlyList<string> videoMids, string offerUfrag = "remoteU")
    {
        var negotiator = new SdpOfferAnswerNegotiator();
        var offer = negotiator.CreateOffer(
            new IPEndPoint(IPAddress.Loopback, 5000), Pcmu, SdpMediaDirection.SendRecv,
            new SdpMediaOptions { Bundle = true, RtcpMux = true, Dtls = OfferDtls(), Ice = OfferIce(offerUfrag), Tracks = Tracks(videoMids) });
        // The answer negotiates every offered video m-line against the shared local video codec (localOptions.Video),
        // so each offered video MID is accepted for sending and TryCreate builds a track for it.
        var answer = negotiator.NegotiateAnswer(
            offer, new IPEndPoint(IPAddress.Loopback, 6000), Pcmu, SdpMediaDirection.SendRecv,
            new SdpMediaOptions
            {
                Bundle = true,
                RtcpMux = true,
                Dtls = AnswerDtls(),
                Ice = AnswerIce(),
                Video = new SdpVideoMediaOptions { Port = 6002, Codecs = [H264] },
            }).Answer!;
        return (offer, answer);
    }

    private static List<SdpTrackOptions> Tracks(IReadOnlyList<string> videoMids)
    {
        var tracks = new List<SdpTrackOptions>
        {
            new() { Kind = "audio", Codecs = Pcmu, Direction = SdpMediaDirection.SendRecv },
        };
        foreach (var _ in videoMids)
            tracks.Add(new SdpTrackOptions { Kind = "video", Codecs = [H264], Direction = SdpMediaDirection.SendRecv });
        return tracks;
    }

    private static WebRtcPeerOptions PeerOptions() => new()
    {
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
        AudioCodecs = Pcmu,
        Dtls = AnswerDtls(),
        Ice = AnswerIce(),
    };

    private static IDtlsSrtpHandshaker Handshaker() => new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance);

    private static SdpDtlsParameters OfferDtls() => new() { Algorithm = "sha-256", Fingerprint = "AA:BB:CC", Setup = "actpass" };
    private static SdpDtlsParameters AnswerDtls() => new() { Algorithm = "sha-256", Fingerprint = "11:22:33" };
    private static SdpIceParameters OfferIce(string ufrag) => new() { Ufrag = ufrag, Pwd = "remotepassword1234567890" };
    private static SdpIceParameters AnswerIce() => new() { Ufrag = "localU", Pwd = "localpassword1234567890" };
}
