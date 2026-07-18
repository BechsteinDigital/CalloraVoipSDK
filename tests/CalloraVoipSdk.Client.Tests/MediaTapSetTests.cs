using CalloraVoipSdk.WebRtc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Client.Tests;

/// <summary>
/// The L3 media-tap dispatch (ADR-012 step 6): <see cref="MediaTapSet"/> fans encoded frames out to attached
/// <see cref="IMediaTap"/>s in both directions, isolates a throwing tap, and stops on detach.
/// </summary>
public sealed class MediaTapSetTests
{
    private static MediaTapSet NewSet() => new(NullLogger.Instance);

    [Fact]
    public void An_attached_tap_observes_outbound_and_inbound_audio()
    {
        var set = NewSet();
        var tap = new RecordingTap();
        set.Attach(tap);

        set.Audio(MediaDirection.Outbound, new byte[] { 1, 2 });
        set.Audio(MediaDirection.Inbound, new byte[] { 3 });

        Assert.Equal(2, tap.Audio.Count);
        Assert.Equal(MediaDirection.Outbound, tap.Audio[0].Direction);
        Assert.Equal(new byte[] { 1, 2 }, tap.Audio[0].Payload);
        Assert.Equal(MediaDirection.Inbound, tap.Audio[1].Direction);
        Assert.Equal(new byte[] { 3 }, tap.Audio[1].Payload);
    }

    [Fact]
    public void Video_direction_timestamp_and_keyframe_are_surfaced()
    {
        var set = NewSet();
        var tap = new RecordingTap();
        set.Attach(tap);

        set.Video(MediaDirection.Inbound, new byte[] { 9 }, 90000, isKeyFrame: true);

        var (direction, frame, ts, key) = Assert.Single(tap.Video);
        Assert.Equal(MediaDirection.Inbound, direction);
        Assert.Equal(new byte[] { 9 }, frame);
        Assert.Equal(90000u, ts);
        Assert.True(key);
    }

    [Fact]
    public void A_detached_tap_is_no_longer_notified()
    {
        var set = NewSet();
        var tap = new RecordingTap();
        var handle = set.Attach(tap);

        set.Audio(MediaDirection.Outbound, new byte[] { 1 });
        handle.Dispose();
        set.Audio(MediaDirection.Outbound, new byte[] { 2 });

        Assert.Single(tap.Audio);   // only the frame before detach
    }

    [Fact]
    public void A_throwing_tap_is_isolated_and_other_taps_still_receive()
    {
        var set = NewSet();
        var healthy = new RecordingTap();
        set.Attach(new ThrowingTap());
        set.Attach(healthy);

        set.Audio(MediaDirection.Inbound, new byte[] { 7 });   // must not throw

        Assert.Single(healthy.Audio);
    }

    [Fact]
    public void An_empty_set_is_a_no_op()
    {
        var set = NewSet();

        set.Audio(MediaDirection.Outbound, new byte[] { 1 });
        set.Video(MediaDirection.Outbound, new byte[] { 2 }, rtpTimestamp: 1, isKeyFrame: false);
        // no throw, nothing to assert beyond reaching here
    }

    private sealed class RecordingTap : IMediaTap
    {
        public List<(MediaDirection Direction, byte[] Payload)> Audio { get; } = [];
        public List<(MediaDirection Direction, byte[] Frame, uint? Timestamp, bool KeyFrame)> Video { get; } = [];

        public void OnAudio(MediaDirection direction, ReadOnlyMemory<byte> payload) => Audio.Add((direction, payload.ToArray()));
        public void OnVideo(MediaDirection direction, ReadOnlyMemory<byte> frame, uint? rtpTimestamp, bool isKeyFrame)
            => Video.Add((direction, frame.ToArray(), rtpTimestamp, isKeyFrame));
    }

    private sealed class ThrowingTap : IMediaTap
    {
        public void OnAudio(MediaDirection direction, ReadOnlyMemory<byte> payload) => throw new InvalidOperationException("boom");
        public void OnVideo(MediaDirection direction, ReadOnlyMemory<byte> frame, uint? rtpTimestamp, bool isKeyFrame) => throw new InvalidOperationException("boom");
    }
}
