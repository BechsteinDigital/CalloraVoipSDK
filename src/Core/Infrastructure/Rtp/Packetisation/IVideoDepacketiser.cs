namespace CalloraVoipSdk.Core.Infrastructure.Rtp.Packetisation;

/// <summary>
/// Reassembles encoded video frames from a stream of RTP payloads (H.264 RFC 6184,
/// VP8 RFC 7741). Stateful per media stream and <b>not thread-safe</b> — bind one
/// instance to exactly one receive loop. Feed payloads in RTP sequence order and call
/// <see cref="Reset"/> on sequence gaps so a fragment of a lost packet is never glued
/// to the next frame. An RTP timestamp change without a preceding marker (senders that
/// omit the marker bit exist) discards the frame under assembly automatically. A
/// malformed or out-of-context payload likewise discards and returns <see langword="false"/>.
/// </summary>
internal interface IVideoDepacketiser
{
    /// <summary>
    /// Processes one RTP payload. Returns <see langword="true"/> with the completed
    /// encoded frame (for H.264: an Annex-B access unit) when <paramref name="marker"/>
    /// closed the frame; <see langword="false"/> while the frame is still assembling or
    /// after a discard.
    /// </summary>
    /// <param name="rtpPayload">The RTP payload (codec payload header + data).</param>
    /// <param name="rtpTimestamp">
    /// The packet's RTP timestamp — all payloads of one frame share it (RFC 6184 §5.1 /
    /// RFC 7741 §4.1); a change signals a new frame even without a marker.
    /// </param>
    /// <param name="marker">The packet's RTP marker bit.</param>
    /// <param name="frame">The completed frame, when the return value is <see langword="true"/>.</param>
    /// <param name="isKeyFrame">
    /// <see langword="true"/> when the completed frame is an intra-coded key frame decodable
    /// without prior frames (VP8 P-bit clear, RFC 7741 §4.3; H.264 IDR NAL type 5, RFC 6184).
    /// Meaningful only when the return value is <see langword="true"/>; <see langword="false"/>
    /// otherwise.
    /// </param>
    bool TryProcess(ReadOnlyMemory<byte> rtpPayload, uint rtpTimestamp, bool marker, out byte[]? frame, out bool isKeyFrame);

    /// <summary>
    /// Discards any frame under assembly — call on RTP sequence gaps so a fragment of a
    /// lost frame is never glued to the next one.
    /// </summary>
    void Reset();

    /// <summary>
    /// Number of RTP payloads discarded as unusable — a malformed payload, an unsupported
    /// packetisation mode, or a fragment/continuation whose frame start was lost. A rising
    /// count signals upstream packet loss or a non-conformant sender; it is not incremented
    /// by the normal markerless frame-boundary reset.
    /// </summary>
    long DiscardedPacketCount { get; }
}
