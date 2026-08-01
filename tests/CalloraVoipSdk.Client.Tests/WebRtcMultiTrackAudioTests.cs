using System.Net;
using CalloraVoipSdk.WebRtc;
using Xunit;

namespace CalloraVoipSdk.Client.Tests;

/// <summary>
/// The public multi-track audio API (4.7.0 N-audio): <see cref="IPeerConnection.AddAudioTrack()"/> adds an audio
/// track (its own m-line, numeric MID) beyond the primary audio anchor and returns an <see cref="IAudioTrack"/>
/// handle; several tracks yield several audio m-lines with numeric MIDs; the receiver materialises one
/// <see cref="RemoteTrack"/> per remote audio m-line. Adding a track after the offer is a pending mid-call add
/// (re-offered on the next cycle). A peer with no AddAudioTrack/AddVideoTrack keeps the byte-identical 1+1 SDP.
/// This mirrors <see cref="WebRtcMultiTrackVideoTests"/> for the audio surface.
/// </summary>
public sealed class WebRtcMultiTrackAudioTests
{
    private static WebRtcClient AudioClient(
        bool enableVideo = false,
        bool useStableNumericMediaIds = false) => new(new WebRtcConfiguration
    {
        EnableVideo = enableVideo,
        UseStableNumericMediaIds = useStableNumericMediaIds,
        // Ephemeral loopback: early-bind gives a live m-line and a fixed port would collide on CI.
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
    });

    // AK#1: the parameterless happy path returns a handle carrying its numeric MID and default direction.
    [Fact]
    public async Task AddAudioTrack_returns_a_handle_with_a_numeric_mid_and_direction()
    {
        var rtc = AudioClient();
        await using var peer = rtc.CreatePeer();

        var extra = peer.AddAudioTrack();

        Assert.NotNull(extra);
        Assert.Equal("1", extra.Mid);                        // primary audio=0, first added audio=1
        Assert.Equal(TrackDirection.SendRecv, extra.Direction);   // parameterless default
    }

    // AK#1: the options overload carries the requested direction onto the handle.
    [Fact]
    public async Task AddAudioTrack_with_options_carries_the_direction()
    {
        var rtc = AudioClient();
        await using var peer = rtc.CreatePeer();

        var extra = peer.AddAudioTrack(new AudioTrackOptions { Direction = TrackDirection.SendOnly });

        Assert.Equal(TrackDirection.SendOnly, extra.Direction);
    }

    // Slice 4 follow-up (Option B): the PUBLIC IAudioTrack.SendFrameAsync(payload, rtpTimestamp) is invokable and
    // routes the outbound payload through the facade's media-tap fan-out (an attached recorder/analytics sees it) —
    // proving the public send path runs, not just the internal seam. The RTP timestamp is threaded onward to the
    // wire (asserted end to end at the BundledMediaSession level in the Core integration tests, since the audio tap
    // — like the primary audio path — does not carry the RTP timestamp). No remote description is applied here, so
    // the peer-level send throws for lack of a session AFTER the tap has already observed the outbound payload; the
    // tap fan-out is what runs first and is the public-handle wiring under test.
    [Fact]
    public async Task Public_audio_track_send_routes_the_payload_through_the_media_tap()
    {
        var rtc = AudioClient();
        await using var peer = rtc.CreatePeer();
        var tap = new RecordingTap();
        using var _ = peer.AttachMediaTap(tap);

        var extra = peer.AddAudioTrack();
        var payload = new byte[] { 9, 8, 7, 6 };
        // The tap runs before the peer-level send; with no BUNDLE session yet the send throws — expected, and it
        // does not undo the tap observation. Catching it keeps the assertion on the public-handle→tap routing.
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await extra.SendFrameAsync(payload, rtpTimestamp: 0xCAFEB0BAu));

        var outbound = Assert.Single(tap.Audio);
        Assert.Equal(MediaDirection.Outbound, outbound.Direction);
        Assert.Equal(payload, outbound.Payload);   // the exact payload passed to the public handle reached the tap
    }

    // AK#2: one AddAudioTrack = two audio m-lines with numeric MIDs, and the handle carries the second MID
    // (so a send on that handle targets a distinct track / SSRC on the wire from the primary audio).
    [Fact]
    public async Task One_added_audio_track_offers_two_audio_m_lines_with_numeric_mids()
    {
        var rtc = AudioClient();
        await using var peer = rtc.CreatePeer();

        var extra = peer.AddAudioTrack();
        var offer = peer.CreateOffer();

        Assert.Equal(2, CountMediaLines(offer, "audio"));
        // Numeric mids on the multi-track path (RFC 8843): primary audio=0, added audio=1.
        Assert.Equal(["0", "1"], MidsInOrder(offer));
        Assert.Equal("1", extra.Mid);
        Assert.DoesNotContain("a=mid:audio", offer, StringComparison.Ordinal);   // no semantic mids on the N-path
    }

    // AK#2: two added audio tracks get distinct sequential MIDs, one per handle.
    [Fact]
    public async Task Two_added_audio_tracks_get_distinct_sequential_mids()
    {
        var rtc = AudioClient();
        await using var peer = rtc.CreatePeer();

        var a = peer.AddAudioTrack();
        var b = peer.AddAudioTrack();
        var offer = peer.CreateOffer();

        Assert.Equal("1", a.Mid);   // primary audio=0, first added=1
        Assert.Equal("2", b.Mid);   // second added=2
        Assert.Equal(3, CountMediaLines(offer, "audio"));
        Assert.Equal(["0", "1", "2"], MidsInOrder(offer));
    }

    // 4.7.2: runtime tracks take stable append-only numeric MIDs in global call order, independent of kind
    // (RFC 8829 — an existing m-line never moves or changes MID). Primary audio (0) and primary video (1) keep
    // their MIDs; an added audio then an added video get 2 and 3 in that order. Pre-4.7.2 the grouped layout put
    // both audios first and both videos last, which drifted a track's MID when the other kind was added later.
    [Fact]
    public async Task Added_tracks_take_stable_append_only_mids_in_call_order()
    {
        var rtc = AudioClient(enableVideo: true);   // EnableVideo primary video present
        await using var peer = rtc.CreatePeer();

        var extraAudio = peer.AddAudioTrack();
        var extraVideo = peer.AddVideoTrack();
        var offer = peer.CreateOffer();

        Assert.Equal("2", extraAudio.Mid);   // primary audio(0), primary video(1), first added track=2
        Assert.Equal("3", extraVideo.Mid);   // second added track=3
        Assert.Equal(2, CountMediaLines(offer, "audio"));
        Assert.Equal(2, CountMediaLines(offer, "video"));
        Assert.Equal(["0", "1", "2", "3"], MidsInOrder(offer));
    }

    // 4.7.0 renegotiation: adding an audio track after the first offer is a pending mid-call add. Its handle already
    // carries the next numeric MID, and a re-offer advertises it, so a full offer/answer cycle applies it to the
    // running session (RFC 8829).
    [Fact]
    public async Task AddAudioTrack_after_the_offer_is_pending_and_re_offered()
    {
        var rtc = AudioClient();
        await using var peer = rtc.CreatePeer();

        var firstOffer = peer.CreateOffer();
        Assert.Equal(1, CountMediaLines(firstOffer, "audio"));   // just the primary audio anchor

        var added = peer.AddAudioTrack();
        Assert.Equal("1", added.Mid);

        var reOffer = peer.CreateOffer();
        Assert.Equal(2, CountMediaLines(reOffer, "audio"));
        Assert.Equal(["0", "1"], MidsInOrder(reOffer));
    }

    // AK#3: a remote description with an additional audio track materialises a distinct RemoteTrack with its MID
    // (beyond the primary audio track), so an SFU-style N-audio offer surfaces one receive track per participant.
    [Fact]
    public async Task Remote_description_with_an_added_audio_track_raises_a_distinct_remote_audio_track()
    {
        // Offerer: primary audio + one added audio (numeric-MID multi-track offer).
        var offererClient = AudioClient();
        await using var offerer = offererClient.CreatePeer();
        offerer.AddAudioTrack();
        var offer = offerer.CreateOffer();

        // Answerer materialises its remote tracks from the applied description (W3C ontrack; no media flows).
        var answererClient = AudioClient();
        await using var answerer = answererClient.CreatePeer();
        var tracks = new List<RemoteTrack>();
        answerer.TrackReceived += (_, t) => tracks.Add(t);

        await answerer.SetRemoteDescriptionAsync(offer);

        var audioTracks = tracks.Where(t => t.Kind == TrackKind.Audio).ToList();
        Assert.Equal(2, audioTracks.Count);                                  // primary + one added remote audio m-line
        Assert.Equal(2, audioTracks.Select(t => t.Mid ?? "").Distinct().Count());   // distinct MID keys
    }

    // AK#4: a peer with no added tracks keeps the byte-identical 1+1 SDP with SEMANTIC mids (unchanged pre-4.7.0).
    [Fact]
    public async Task No_added_track_keeps_the_semantic_mid_offer()
    {
        var rtc = AudioClient(enableVideo: true);
        await using var peer = rtc.CreatePeer();

        var offer = peer.CreateOffer();

        Assert.Contains("a=mid:audio", offer, StringComparison.Ordinal);
        Assert.Contains("a=mid:video", offer, StringComparison.Ordinal);
        Assert.DoesNotContain("a=mid:0", offer, StringComparison.Ordinal);   // no numeric-MID multi-track path
        Assert.Equal(1, CountMediaLines(offer, "audio"));
    }

    [Fact]
    public async Task Stable_numeric_mode_preserves_primary_mids_and_appends_interleaved_tracks()
    {
        var rtc = AudioClient(enableVideo: true, useStableNumericMediaIds: true);
        await using var peer = rtc.CreatePeer();

        var firstOffer = peer.CreateOffer();

        Assert.Equal(["0", "1"], MidsInOrder(firstOffer));

        // The conference router adds one source as video+audio. Both new m-lines must append after the
        // already-negotiated primary pair; changing the primary video from MID 1 would make browsers reject
        // the re-offer because RFC 8829 requires existing m-line order/MIDs to remain stable.
        var extraVideo = peer.AddVideoTrack();
        var extraAudio = peer.AddAudioTrack();
        var reOffer = peer.CreateOffer();

        Assert.Equal("2", extraVideo.Mid);
        Assert.Equal("3", extraAudio.Mid);
        Assert.Equal(["0", "1", "2", "3"], MidsInOrder(reOffer));
        Assert.Equal(["audio", "video", "video", "audio"], MediaKindsInOrder(reOffer));
    }

    // Records the outbound audio the facade fans out to attached taps, for the public-send routing assertion.
    private sealed class RecordingTap : IMediaTap
    {
        public List<(MediaDirection Direction, byte[] Payload)> Audio { get; } = [];

        public void OnAudio(MediaDirection direction, ReadOnlyMemory<byte> payload) => Audio.Add((direction, payload.ToArray()));
        public void OnVideo(MediaDirection direction, ReadOnlyMemory<byte> frame, uint? rtpTimestamp, bool isKeyFrame, string? rid) { }
    }

    // ── SDP helpers (mirrors WebRtcMultiTrackVideoTests) ─────────────────────

    private static int CountMediaLines(string sdp, string media)
        => sdp.Split('\n').Count(l => l.StartsWith($"m={media} ", StringComparison.Ordinal));

    private static string[] MidsInOrder(string sdp)
        => sdp.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("a=mid:", StringComparison.Ordinal))
            .Select(l => l["a=mid:".Length..])
            .ToArray();

    private static string[] MediaKindsInOrder(string sdp)
        => sdp.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("m=", StringComparison.Ordinal))
            .Select(l => l[2..].Split(' ', 2)[0])
            .ToArray();
}
