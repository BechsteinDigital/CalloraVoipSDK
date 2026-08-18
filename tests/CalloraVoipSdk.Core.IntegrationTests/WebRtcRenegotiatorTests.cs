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
/// outbound SSRCs, so an added track's SSRC never collides a running one (RFC 3550 §8.1). ICE restart handling
/// (#226) is covered at the peer level, where a full cycle can be driven.
/// </summary>
public sealed class WebRtcRenegotiatorTests
{
    private static readonly IReadOnlyList<SdpCodecDefinition> Pcmu =
        [new SdpCodecDefinition { PayloadType = 0, Name = "PCMU", ClockRate = 8000 }];
    private static readonly SdpIceParameters LocalIce = new() { Ufrag = "localU", Pwd = "localPassword1234567890" };
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
        var renegotiator = new WebRtcRenegotiator(NullLoggerFactory.Instance, opaqueVideoFrames: false, LocalIce);

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

    /// <summary>
    /// #223 / ADR-068: a track added mid-call inherits the owning peer's opaque-video-frames policy. The policy is
    /// not in the SDP, so it cannot be re-derived from the re-offer — the renegotiator holds it for the peer's
    /// lifetime. Without this, an end-to-end encrypted peer would get a clear-media track on renegotiation, whose
    /// depacketiser reads ciphertext as codec syntax.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Diff_gives_an_added_video_track_the_peers_opaque_frame_policy(bool opaque)
    {
        var (offer, answer) = Exchange(videoMids: ["1"]);
        await using var session = BuildSession(offer, answer);

        var (reOffer, reAnswer) = Exchange(videoMids: ["1", "2"]);
        var renegotiator = new WebRtcRenegotiator(NullLoggerFactory.Instance, opaqueVideoFrames: opaque, LocalIce);

        var diff = renegotiator.ComputeDiff(session, reAnswer, reOffer);

        var added = Assert.Single(diff.TracksToAdd);
        Assert.Equal(opaque, added.OpaqueVideoFrames);
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
        var renegotiator = new WebRtcRenegotiator(NullLoggerFactory.Instance, opaqueVideoFrames: false, LocalIce);

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
        var diff = new WebRtcRenegotiator(NullLoggerFactory.Instance, opaqueVideoFrames: false, LocalIce).ComputeDiff(session, reAnswer, reOffer);

        Assert.True(diff.IsEmpty);
    }

    [Fact]
    public async Task An_ice_restart_re_offer_restarts_the_session_ice_and_reports_it()
    {
        var (offer, answer) = Exchange(videoMids: ["1"]);
        await using var session = BuildSession(offer, answer);

        // Re-offer rotates the remote (offer) ICE ufrag → an ICE restart (#226, RFC 8445 §9).
        var (reOffer, reAnswer) = Exchange(videoMids: ["1"], offerUfrag: "restartedUfrag");
        var restarted = false;
        var renegotiator = new WebRtcRenegotiator(
            NullLoggerFactory.Instance, opaqueVideoFrames: false, LocalIce, onIceRestarted: () => restarted = true);

        await renegotiator.ApplyReAnswerAsync(session, reAnswer, reOffer);

        // The session now answers to the peer's new credentials, and the peer was told so it can transition
        // its connection state instead of staying wherever the network change left it.
        Assert.Equal("restartedUfrag", session.RemoteIceUfrag);
        Assert.True(restarted, "the peer must be told about the applied ICE restart");
    }

    [Fact]
    public async Task A_re_offer_without_rotated_credentials_is_not_treated_as_a_restart()
    {
        var (offer, answer) = Exchange(videoMids: ["1"]);
        await using var session = BuildSession(offer, answer);

        // Same credentials — the common mid-call track change. Restarting here would tear down a working check
        // list on every renegotiation.
        var (reOffer, reAnswer) = Exchange(videoMids: ["1", "2"]);
        var restarted = false;
        var renegotiator = new WebRtcRenegotiator(
            NullLoggerFactory.Instance, opaqueVideoFrames: false, LocalIce, onIceRestarted: () => restarted = true);

        await renegotiator.ApplyReAnswerAsync(session, reAnswer, reOffer);

        Assert.False(restarted);
        Assert.Equal(["1", "2"], session.VideoMids); // the track diff still ran
    }

    [Fact]
    public async Task Diff_adds_a_new_additional_audio_track_with_an_ssrc_distinct_from_the_live_session()
    {
        // Start with one audio m-line (the primary anchor, MID "0") — no additional audio yet.
        var (offer, answer) = AudioExchange(audioMids: ["0"]);
        await using var session = BuildSession(offer, answer);
        Assert.Empty(session.AudioMids);
        Assert.Equal("0", session.PrimaryAudioMid);
        var liveSsrcs = session.OutboundSsrcs;

        // Re-offer adds a SECOND audio m-line (MID "1"); both sides send-recv, so it is an inbound additional track.
        var (reOffer, reAnswer) = AudioExchange(audioMids: ["0", "1"]);
        var renegotiator = new WebRtcRenegotiator(NullLoggerFactory.Instance, opaqueVideoFrames: false, LocalIce);

        var diff = renegotiator.ComputeDiff(session, reAnswer, reOffer);

        // Exactly MID "1" is a new additional audio track with a distinct SSRC; no removals, no video changes.
        Assert.Empty(diff.AudioMidsToDeactivate);
        Assert.Empty(diff.TracksToAdd);
        Assert.Empty(diff.MidsToDeactivate);
        var added = Assert.Single(diff.AudioTracksToAdd);
        Assert.Equal("1", added.Mid);
        Assert.DoesNotContain(added.Ssrc, liveSsrcs); // RFC 3550 §8.1: distinct from every running SSRC

        renegotiator.Apply(session, diff);
        Assert.Equal(["1"], session.AudioMids); // now a live additional audio track
        Assert.Contains(added.Ssrc, session.OutboundSsrcs);
    }

    [Fact]
    public async Task Diff_adds_a_new_outbound_audio_track_when_the_remote_answer_is_recvonly()
    {
        // The server starts with the primary audio anchor only.
        var (offer, answer) = AudioExchange(audioMids: ["0"]);
        await using var session = BuildOffererSession(offer, answer);
        Assert.Empty(session.AudioMids);

        // SFU re-offer: MID "1" sends from this peer only; the browser accepts it as recv-only.
        var (reOffer, reAnswer) = AudioExchange(
            audioMids: ["0", "1"],
            additionalDirection: SdpMediaDirection.SendOnly);
        var renegotiator = new WebRtcRenegotiator(NullLoggerFactory.Instance, opaqueVideoFrames: false, LocalIce);

        var diff = renegotiator.ComputeDiff(session, reOffer, reAnswer);

        var added = Assert.Single(diff.AudioTracksToAdd);
        Assert.Equal("1", added.Mid);

        renegotiator.Apply(session, diff);
        Assert.Equal(["1"], session.AudioMids);
    }

    [Fact]
    public async Task Diff_deactivates_an_additional_audio_track_the_re_offer_dropped()
    {
        // Start with two audio m-lines: the anchor "0" plus one additional "1".
        var (offer, answer) = AudioExchange(audioMids: ["0", "1"]);
        await using var session = BuildSession(offer, answer);
        Assert.Equal(["1"], session.AudioMids);

        // Re-offer keeps only the anchor "0" (drops the additional "1").
        var (reOffer, reAnswer) = AudioExchange(audioMids: ["0"]);
        var renegotiator = new WebRtcRenegotiator(NullLoggerFactory.Instance, opaqueVideoFrames: false, LocalIce);

        var diff = renegotiator.ComputeDiff(session, reAnswer, reOffer);

        Assert.Empty(diff.AudioTracksToAdd);
        Assert.Equal(["1"], diff.AudioMidsToDeactivate);

        renegotiator.Apply(session, diff);
        Assert.Empty(session.AudioMids); // the dropped additional audio track is gone from the live session
    }

    [Fact]
    public async Task The_primary_audio_anchor_is_never_diffed_or_deactivated()
    {
        // Anchor protection: start with two additional audio tracks ("1","2") on top of the anchor "0".
        var (offer, answer) = AudioExchange(audioMids: ["0", "1", "2"]);
        await using var session = BuildSession(offer, answer);
        Assert.Equal(["1", "2"], session.AudioMids);
        Assert.Equal("0", session.PrimaryAudioMid);

        // A re-offer that drops EVERY audio m-line except the anchor "0" must deactivate the two additional tracks
        // but NEVER the anchor — the anchor is not in AudioMids, so it can never appear in the deactivate list.
        var (reOffer, reAnswer) = AudioExchange(audioMids: ["0"]);
        var renegotiator = new WebRtcRenegotiator(NullLoggerFactory.Instance, opaqueVideoFrames: false, LocalIce);

        var diff = renegotiator.ComputeDiff(session, reAnswer, reOffer);

        Assert.DoesNotContain("0", diff.AudioMidsToDeactivate); // the anchor is never deactivated
        Assert.Equal(new[] { "1", "2" }, diff.AudioMidsToDeactivate.OrderBy(m => m, StringComparer.Ordinal));

        renegotiator.Apply(session, diff);
        Assert.Empty(session.AudioMids);
        // The primary anchor still carries the mid-less audio path (it was never a diffable/additional track).
        Assert.Equal("0", session.PrimaryAudioMid);

        // Directly deactivating the anchor MID is a no-op (belt-and-suspenders anchor protection at the session).
        session.SetAudioTrackInactive("0");
        Assert.Equal("0", session.PrimaryAudioMid);
        // Adding the anchor MID as an additional track is rejected.
        Assert.Throws<InvalidOperationException>(() =>
            session.AddAudioTrack(new BundledTrackConfig { Mid = "0", Ssrc = 0x1234, PayloadType = 0, SamplesPerPacket = 160 }));
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

    // Same exchange from the offerer's perspective: localDescription is the offer and remoteDescription the answer.
    private static BundledMediaSession BuildOffererSession(SdpSessionDescription offer, SdpSessionDescription answer)
    {
        var session = WebRtcSessionFactory.TryCreate(
            answer, offer, PeerOptions(), Handshaker(), DtlsCertificate.GenerateEcdsaP256(), NullLoggerFactory.Instance);
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

    // A BUNDLE offer/answer with N send-recv audio m-lines (the SFU pattern): the first is the primary anchor, the
    // rest are additional inbound audio tracks. Numeric MIDs "0","1",… come from the track order. Both sides
    // send-recv, so every audio m-line negotiates for receiving and TryBuildAudioTrack builds a sink for each
    // beyond the anchor. The answer mirrors the offer's audio tracks so each additional MID is present locally too.
    private static (SdpSessionDescription Offer, SdpSessionDescription Answer) AudioExchange(
        IReadOnlyList<string> audioMids,
        string offerUfrag = "remoteU",
        SdpMediaDirection additionalDirection = SdpMediaDirection.SendRecv)
    {
        var negotiator = new SdpOfferAnswerNegotiator();
        var audioTracks = audioMids
            .Select((_, index) => new SdpTrackOptions
            {
                Kind = "audio",
                Codecs = Pcmu,
                Direction = index == 0 ? SdpMediaDirection.SendRecv : additionalDirection,
            })
            .ToList();
        var offer = negotiator.CreateOffer(
            new IPEndPoint(IPAddress.Loopback, 5000), Pcmu, SdpMediaDirection.SendRecv,
            new SdpMediaOptions { Bundle = true, RtcpMux = true, Dtls = OfferDtls(), Ice = OfferIce(offerUfrag), Tracks = audioTracks });
        var answer = negotiator.NegotiateAnswer(
            offer, new IPEndPoint(IPAddress.Loopback, 6000), Pcmu, SdpMediaDirection.SendRecv,
            new SdpMediaOptions { Bundle = true, RtcpMux = true, Dtls = AnswerDtls(), Ice = AnswerIce() }).Answer!;
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
