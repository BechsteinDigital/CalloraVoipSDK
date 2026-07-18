namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// An L3 observer of the encoded media flowing through an <see cref="IPeerConnection"/> in both directions
/// — the extension seam for recording, analytics, or feeding an AI pipeline without owning the peer. Attach
/// with <see cref="IPeerConnection.AttachMediaTap"/> and dispose the handle to detach.
/// </summary>
/// <remarks>
/// Transport-only: payloads are the raw depacketised codec bitstream. A tap is invoked on the media path,
/// so it must return quickly and should not throw — a throwing tap is caught, logged, and isolated from the
/// media flow rather than being allowed to break it.
/// </remarks>
public interface IMediaTap
{
    /// <summary>Observes one audio payload in the given <paramref name="direction"/>.</summary>
    void OnAudio(MediaDirection direction, ReadOnlyMemory<byte> payload);

    /// <summary>
    /// Observes one video frame in the given <paramref name="direction"/>. <paramref name="rtpTimestamp"/>
    /// is <see langword="null"/> when unknown; <paramref name="isKeyFrame"/> is <see langword="false"/> for
    /// outbound frames (the send path does not carry the flag). <paramref name="rid"/> is the simulcast layer
    /// id (RFC 8853) for an outbound simulcast frame, so a tap can tell the layers apart; it is
    /// <see langword="null"/> for a single-stream send and for inbound frames (receive-side RID demux is a
    /// later slice). Richer per-frame metadata (MID/SSRC/track ids) will follow when those are surfaced.
    /// </summary>
    void OnVideo(MediaDirection direction, ReadOnlyMemory<byte> frame, uint? rtpTimestamp, bool isKeyFrame, string? rid);
}
