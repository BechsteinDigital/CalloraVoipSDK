using CalloraVoipSdk.WebRtc;
using Xunit;

namespace CalloraVoipSdk.Client.Tests;

/// <summary>
/// [Client] #166 P1-4: RemoteTrackSet keys a track per MID and never removes it, so a sequence of reoffers
/// carrying fresh MIDs would grow the retained set over the peer's whole lifetime — unbounded by any single-SDP
/// cap. These tests pin the cumulative per-kind cap: past it a new MID is neither retained nor surfaced, while
/// already-materialised MIDs still route.
/// </summary>
public sealed class RemoteTrackSetRetentionCapTests
{
    private const int Cap = 128;

    [Fact]
    public void Distinct_audio_mids_beyond_the_cap_are_not_retained_or_surfaced()
    {
        var raised = 0;
        var set = new RemoteTrackSet(_ => raised++);

        RemoteTrack? last = null;
        for (var i = 0; i < Cap + 50; i++)
            last = set.EnsureAudioTrack($"mid-{i}", streamId: null, trackId: null);

        Assert.Equal(Cap, raised);   // exactly the cap materialised and surfaced via the callback
        Assert.Null(last);           // a fresh MID past the cap is dropped
    }

    [Fact]
    public void An_already_materialised_mid_still_routes_after_the_cap_is_reached()
    {
        var set = new RemoteTrackSet(_ => { });
        for (var i = 0; i < Cap; i++)
            set.EnsureAudioTrack($"mid-{i}", null, null);

        Assert.NotNull(set.EnsureAudioTrack("mid-0", null, null));   // existing MID still returns its track
        Assert.Null(set.EnsureAudioTrack("mid-new", null, null));    // new MID past the cap is dropped
    }

    [Fact]
    public void Audio_and_video_caps_are_independent()
    {
        var set = new RemoteTrackSet(_ => { });
        for (var i = 0; i < Cap; i++)
            set.EnsureAudioTrack($"a-{i}", null, null);

        // The audio cap does not consume the video budget.
        Assert.NotNull(set.EnsureVideoTrack("v-0", null, null));
    }
}
