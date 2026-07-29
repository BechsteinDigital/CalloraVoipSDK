using System.Net;
using CalloraVoipSdk.WebRtc;
using Xunit;

namespace CalloraVoipSdk.Client.Tests;

/// <summary>
/// The public multi-track video API (4.7.0 P2c): <see cref="IPeerConnection.AddVideoTrack()"/> adds a video
/// track (its own m-line, numeric MID) before the first offer and returns an <see cref="IVideoTrack"/> handle;
/// several tracks yield several video m-lines with numeric MIDs; the receiver materialises one
/// <see cref="RemoteTrack"/> per remote video m-line. Adding a track after the offer is produced throws (P3
/// mid-call add / renegotiation). A peer with only <see cref="WebRtcConfiguration.EnableVideo"/> and no
/// AddVideoTrack keeps the byte-identical 1+1 SDP (semantic mids "audio"/"video").
/// </summary>
public sealed class WebRtcMultiTrackVideoTests
{
    private static WebRtcClient VideoClient(bool enableVideo = true) => new(new WebRtcConfiguration
    {
        EnableVideo = enableVideo,
        // Ephemeral loopback: early-bind gives a live m-line and a fixed port would collide on CI.
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
    });

    // AK#1: the parameterless happy path returns a handle carrying its MID and direction.
    [Fact]
    public async Task AddVideoTrack_returns_a_handle_with_a_mid_and_direction()
    {
        var rtc = VideoClient(enableVideo: false);
        await using var peer = rtc.CreatePeer();

        var cam = peer.AddVideoTrack();

        Assert.NotNull(cam);
        Assert.False(string.IsNullOrEmpty(cam.Mid));
        Assert.Equal(TrackDirection.SendRecv, cam.Direction);   // parameterless default
    }

    // AK#1: the options overload carries the requested direction onto the handle.
    [Fact]
    public async Task AddVideoTrack_with_options_carries_the_direction()
    {
        var rtc = VideoClient(enableVideo: false);
        await using var peer = rtc.CreatePeer();

        var screen = peer.AddVideoTrack(new VideoTrackOptions { Direction = TrackDirection.SendOnly });

        Assert.Equal(TrackDirection.SendOnly, screen.Direction);
    }

    // 4.7.0 renegotiation: adding a track after the first offer is now allowed (mid-call add). The track is
    // pending — its handle already carries the next numeric MID — and a subsequent re-offer advertises it, so a
    // full offer/answer cycle can apply it to the running session (RFC 8829). This replaces the former P3 boundary
    // (mid-call add throwing), which is no longer the behaviour.
    [Fact]
    public async Task AddVideoTrack_after_the_offer_is_allowed_and_re_offered()
    {
        var rtc = VideoClient();   // EnableVideo primary → audio=0, primary video=1
        await using var peer = rtc.CreatePeer();

        var firstOffer = peer.CreateOffer();
        Assert.Equal(1, CountMediaLines(firstOffer, "video"));

        // Mid-call add: no longer throws; returns a handle with the next numeric MID (pending until re-offer).
        var added = peer.AddVideoTrack();
        Assert.Equal("2", added.Mid);

        // The re-offer advertises the newly added track (the renegotiation offer the peer would send).
        var reOffer = peer.CreateOffer();
        Assert.Equal(2, CountMediaLines(reOffer, "video"));
        Assert.Equal(["0", "1", "2"], MidsInOrder(reOffer));
    }

    // AK#2: EnableVideo + one AddVideoTrack = two video m-lines with numeric MIDs, and the handles carry
    // distinct MIDs (so a send on handle A vs B targets distinct tracks / SSRCs on the wire).
    [Fact]
    public async Task EnableVideo_plus_one_added_track_offers_two_video_m_lines_with_distinct_numeric_mids()
    {
        var rtc = VideoClient();   // EnableVideo primary + one added = two video tracks
        await using var peer = rtc.CreatePeer();

        var extra = peer.AddVideoTrack();
        var offer = peer.CreateOffer();

        Assert.Equal(2, CountMediaLines(offer, "video"));
        // Numeric mids on the multi-track path (RFC 8843): audio=0, primary video=1, added video=2.
        Assert.Equal(["0", "1", "2"], MidsInOrder(offer));
        Assert.Equal("2", extra.Mid);                                  // the added track's handle knows its MID
        Assert.DoesNotContain("a=mid:audio", offer, StringComparison.Ordinal);   // no semantic mids on the N-path
        Assert.DoesNotContain("a=mid:video", offer, StringComparison.Ordinal);
    }

    // AK#2: two added tracks (no EnableVideo) get distinct MIDs, one per handle.
    [Fact]
    public async Task Two_added_tracks_get_distinct_mids()
    {
        var rtc = VideoClient(enableVideo: false);
        await using var peer = rtc.CreatePeer();

        var cam = peer.AddVideoTrack();
        var screen = peer.AddVideoTrack();
        var offer = peer.CreateOffer();

        Assert.NotEqual(cam.Mid, screen.Mid);
        Assert.Equal("1", cam.Mid);       // audio=0, first video=1
        Assert.Equal("2", screen.Mid);    // second video=2
        Assert.Equal(2, CountMediaLines(offer, "video"));
    }

    // AK#3: a remote description with N video tracks materialises N distinct RemoteTracks, each with its MID.
    [Fact]
    public async Task Remote_description_with_two_video_tracks_raises_two_distinct_remote_video_tracks()
    {
        // Offerer: two video tracks (numeric-MID multi-track offer).
        var offererClient = VideoClient();
        await using var offerer = offererClient.CreatePeer();
        offerer.AddVideoTrack();
        var offer = offerer.CreateOffer();

        // Answerer materialises its remote tracks from the applied description (W3C ontrack; no media flows).
        var answererClient = new WebRtcClient(new WebRtcConfiguration { EnableVideo = true });
        await using var answerer = answererClient.CreatePeer();
        var tracks = new List<RemoteTrack>();
        answerer.TrackReceived += (_, t) => tracks.Add(t);

        await answerer.SetRemoteDescriptionAsync(offer);

        var videoTracks = tracks.Where(t => t.Kind == TrackKind.Video).ToList();
        Assert.Equal(2, videoTracks.Count);                                  // one per remote video m-line
        Assert.Equal(2, videoTracks.Select(t => t.Mid).Distinct().Count());  // distinct MIDs
        Assert.All(videoTracks, t => Assert.False(string.IsNullOrEmpty(t.Mid)));
        Assert.Contains(tracks, t => t.Kind == TrackKind.Audio);             // audio track still surfaced
    }

    // AK#4: a peer with only EnableVideo (no AddVideoTrack) keeps the byte-identical 1+1 SDP with SEMANTIC mids.
    [Fact]
    public async Task EnableVideo_only_keeps_the_semantic_mid_1plus1_offer()
    {
        var rtc = VideoClient();
        await using var peer = rtc.CreatePeer();

        var offer = peer.CreateOffer();

        Assert.Contains("a=mid:audio", offer, StringComparison.Ordinal);
        Assert.Contains("a=mid:video", offer, StringComparison.Ordinal);
        Assert.DoesNotContain("a=mid:0", offer, StringComparison.Ordinal);   // no numeric-MID multi-track path
        Assert.Equal("BUNDLE audio video", GroupLine(offer));
        Assert.Equal(1, CountMediaLines(offer, "video"));
    }

    // ── SDP helpers ──────────────────────────────────────────────────────────

    private static int CountMediaLines(string sdp, string media)
        => sdp.Split('\n').Count(l => l.StartsWith($"m={media} ", StringComparison.Ordinal));

    private static string[] MidsInOrder(string sdp)
        => sdp.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("a=mid:", StringComparison.Ordinal))
            .Select(l => l["a=mid:".Length..])
            .ToArray();

    private static string? GroupLine(string sdp)
        => sdp.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("a=group:", StringComparison.Ordinal))
            .Select(l => l["a=group:".Length..])
            .FirstOrDefault();
}
