using CalloraVoipSdk.Core.Infrastructure.Rtp.Packetisation;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// RTP-F4: the video depacketisers expose a discard counter so silent frame drops
/// (malformed payloads, unsupported packetisation, fragments whose start was lost) become
/// observable. A rising count signals upstream loss or a non-conformant sender; a clean
/// frame must not move it.
/// </summary>
public sealed class VideoDepacketiserDiscardTelemetryTests
{
    [Fact]
    public void Vp8_malformed_descriptor_is_counted()
    {
        var depacketiser = new Vp8Depacketiser();

        // One byte: descriptor without a payload byte — cannot be stripped, so it is discarded.
        Assert.False(depacketiser.TryProcess(new byte[] { 0x10 }, rtpTimestamp: 1, marker: true, out _, out _));
        Assert.Equal(1, depacketiser.DiscardedPacketCount);
    }

    [Fact]
    public void Vp8_continuation_without_frame_start_is_counted()
    {
        var depacketiser = new Vp8Depacketiser();

        // S=0 descriptor while no frame is assembling (the frame start was lost): dropped.
        Assert.False(depacketiser.TryProcess(new byte[] { 0x00, 0xAA }, rtpTimestamp: 1, marker: false, out _, out _));
        Assert.Equal(1, depacketiser.DiscardedPacketCount);
    }

    [Fact]
    public void Vp8_clean_keyframe_does_not_move_the_counter()
    {
        var depacketiser = new Vp8Depacketiser();

        // S=1/PID=0 descriptor, VP8 header P=0 (key frame), one payload byte, marker set.
        Assert.True(depacketiser.TryProcess(new byte[] { 0x10, 0x00, 0x2A }, rtpTimestamp: 1, marker: true, out var frame, out var isKeyFrame));
        Assert.NotNull(frame);
        Assert.True(isKeyFrame);
        Assert.Equal(0, depacketiser.DiscardedPacketCount);
    }

    [Fact]
    public void H264_empty_payload_is_counted()
    {
        var depacketiser = new H264Depacketiser();

        Assert.False(depacketiser.TryProcess(Array.Empty<byte>(), rtpTimestamp: 1, marker: true, out _, out _));
        Assert.Equal(1, depacketiser.DiscardedPacketCount);
    }

    [Fact]
    public void H264_unsupported_packetisation_mode_is_counted()
    {
        var depacketiser = new H264Depacketiser();

        // NAL type 26 (MTAP16) is not a supported mode (only 1-23, STAP-A/24, FU-A/28): discarded.
        Assert.False(depacketiser.TryProcess(new byte[] { 0x1A, 0x00 }, rtpTimestamp: 1, marker: true, out _, out _));
        Assert.Equal(1, depacketiser.DiscardedPacketCount);
    }
}
