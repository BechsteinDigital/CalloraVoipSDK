using System.Net;
using CalloraVoipSdk.WebRtc;
using Xunit;

namespace CalloraVoipSdk.Client.Tests;

/// <summary>
/// Facade correctness fixes: distinct video/RTX payload types (H.264 vs VP8 no longer collide on PT 96),
/// remote tracks materialised from the remote description (W3C ontrack) rather than on the first frame, and
/// the RemoteTrackSet ensure/deliver split that backs it.
/// </summary>
public sealed class WebRtcFacadeCorrectnessTests
{
    [Fact]
    public async Task Video_offer_uses_distinct_payload_types_and_advertises_rtx()
    {
        var rtc = new WebRtcClient(new WebRtcConfiguration
        {
            EnableVideo = true,
            VideoCodecs = ["H264", "VP8"],
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 40221),
        });
        await using var peer = rtc.CreatePeer();

        var offer = peer.CreateOffer();

        Assert.Contains("H264/90000", offer, StringComparison.Ordinal);
        Assert.Contains("VP8/90000", offer, StringComparison.Ordinal);
        Assert.NotEqual(PayloadType(offer, "H264"), PayloadType(offer, "VP8"));   // the PT-96 collision is gone
        Assert.Contains("rtx/90000", offer, StringComparison.Ordinal);           // RTX repair codec (RFC 4588)
        Assert.Contains("apt=", offer, StringComparison.Ordinal);                // ties RTX to its codec
    }

    [Fact]
    public void An_unknown_video_codec_is_rejected()
    {
        var rtc = new WebRtcClient(new WebRtcConfiguration { EnableVideo = true, VideoCodecs = ["H265"] });

        Assert.Throws<ArgumentException>(() => rtc.CreatePeer());
    }

    [Fact]
    public void Ensure_materialises_a_track_once_without_a_frame_and_delivery_reuses_it()
    {
        var raised = new List<RemoteTrack>();
        var frames = new List<EncodedFrame>();
        var set = new RemoteTrackSet(track =>
        {
            raised.Add(track);
            track.FrameReceived += (_, f) => frames.Add(f);
        });

        var track = set.EnsureAudioTrack("stream-1", "track-a");   // materialise, no frame yet

        Assert.Single(raised);
        Assert.Empty(frames);
        Assert.Equal("stream-1", track.StreamId);

        set.DeliverAudioFrame("stream-1", "track-a", new EncodedFrame(new byte[] { 1 }, null, false, null));

        Assert.Single(raised);   // no second TrackReceived — the existing track is reused
        Assert.Single(frames);   // the frame reached the pre-materialised track
    }

    [Fact]
    public async Task Remote_tracks_are_raised_when_the_remote_description_is_applied()
    {
        var offererClient = new WebRtcClient(new WebRtcConfiguration
        {
            EnableVideo = true,
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 40222),
        });
        var answererClient = new WebRtcClient();
        await using var offerer = offererClient.CreatePeer();
        await using var answerer = answererClient.CreatePeer();

        var tracks = new List<RemoteTrack>();
        answerer.TrackReceived += (_, track) => tracks.Add(track);

        var offer = offerer.CreateOffer();
        await answerer.SetRemoteDescriptionAsync(offer);   // no StartAsync — no media has flowed yet

        Assert.Contains(tracks, t => t.Kind == TrackKind.Audio);
        Assert.Contains(tracks, t => t.Kind == TrackKind.Video);
    }

    // The payload type of the a=rtpmap line for a codec name (e.g. "a=rtpmap:97 H264/90000" -> 97).
    private static int PayloadType(string sdp, string codec)
        => sdp.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("a=rtpmap:", StringComparison.Ordinal) && line.Contains($" {codec}/", StringComparison.Ordinal))
            .Select(line => int.Parse(line["a=rtpmap:".Length..].Split(' ')[0]))
            .First();
}
