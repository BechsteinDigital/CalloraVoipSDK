using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.WebRtc;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Stable append-only MID assignment for runtime-added WebRTC tracks (4.7.2, RFC 8829). Guards the Finding 2
/// regression: under the pre-4.7.2 grouped layout a video track added before an audio track drifted onto the
/// audio's MID, so <c>VideoTrack.SendFrameAsync</c> addressed the wrong m-line. MIDs are now assigned in global
/// API call order, independent of track kind, so mixed add order can never collide.
/// </summary>
public sealed class WebRtcAddedTrackSetTests
{
    private static readonly IReadOnlyList<SdpCodecDefinition> AudioCodecs =
        [new() { PayloadType = 0, Name = "PCMU", ClockRate = 8000 }];

    private static readonly IReadOnlyList<SdpCodecDefinition> VideoCodecs =
        [new() { PayloadType = 96, Name = "VP8", ClockRate = 90000 }];

    private static WebRtcAddedAudioTrack Audio() => new() { Codecs = AudioCodecs };
    private static WebRtcAddedVideoTrack Video() => new() { Codecs = VideoCodecs };

    [Fact]
    public void Video_added_before_audio_gets_distinct_stable_mids()
    {
        // Pre-4.7.2 the grouped layout returned "1" for AddVideo and "1" again for the later AddAudio.
        var set = new WebRtcAddedTrackSet(primaryVideoCount: 0);

        var videoMid = set.AddVideo(Video());
        var audioMid = set.AddAudio(Audio());

        Assert.Equal("1", videoMid);   // first appended track (call order 0), no primary video reserved
        Assert.Equal("2", audioMid);   // second appended track (call order 1)
        Assert.NotEqual(videoMid, audioMid);
    }

    [Fact]
    public void Mids_follow_call_order_regardless_of_kind_and_primary_video()
    {
        // With a primary video the primary audio is MID 0 and the primary video MID 1; appended runtime tracks
        // start at MID 2 in exact call order, independent of audio/video kind (RFC 8829 append-only).
        var set = new WebRtcAddedTrackSet(primaryVideoCount: 1);

        Assert.Equal("2", set.AddAudio(Audio()));
        Assert.Equal("3", set.AddVideo(Video()));
        Assert.Equal("4", set.AddAudio(Audio()));
    }
}
