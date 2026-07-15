using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.Application.Media;

/// <summary>
/// Negotiated video sub-stream of one call media session (WebRTC phase 2): carries
/// already-encoded video frames over RTP with the codec's payload format (H.264
/// RFC 6184 / VP8 RFC 7741). Encoding/decoding is the caller's concern until the
/// codec abstraction lands — this stream moves encoded access units/frames.
/// </summary>
internal interface IVideoMediaStream
{
    /// <summary>Normalized negotiated codec name, e.g. <c>VP8</c> or <c>H264</c>.</summary>
    string CodecName { get; }

    /// <summary>Negotiated video RTP payload type.</summary>
    int PayloadType { get; }

    /// <summary>
    /// Sends one encoded video frame (for H.264: one Annex-B access unit), packetised
    /// per the negotiated payload format. All packets of the frame share
    /// <paramref name="rtpTimestamp"/> (90 kHz clock); the marker bit is set on the last.
    /// Concurrent calls are serialized — packets of two frames never interleave.
    /// </summary>
    Task SendFrameAsync(ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken ct = default);

    /// <summary>
    /// Raised when one complete inbound video frame was reassembled: the encoded frame
    /// bytes plus its RTP timestamp (90 kHz). Frames with lost packets are discarded,
    /// never delivered partially.
    /// </summary>
    event Action<byte[], uint>? FrameReceived;

    /// <summary>
    /// Raised when the peer requested a keyframe via RTCP PLI/FIR (RFC 4585/5104) — the
    /// encoder feeding <see cref="SendFrameAsync"/> should emit an intra frame next. The
    /// stream itself sends a PLI to the peer automatically when it detects inbound loss.
    /// </summary>
    event Action? KeyFrameRequested;

    /// <summary>
    /// The current SDK-recommended outbound video bitrate in bits per second, or
    /// <see langword="null"/> when transport-cc congestion control is inactive for this leg (the peer
    /// did not negotiate the a=extmap). Updated on each feedback report; the application sets its
    /// encoder to this value (transport-only — the SDK never encodes).
    /// </summary>
    long? RecommendedBitrateBps { get; }

    /// <summary>
    /// The current coarse network-quality indicator, or <see langword="null"/> when congestion control
    /// is inactive for this leg.
    /// </summary>
    NetworkQuality? NetworkQuality { get; }

    /// <summary>
    /// Raised after a feedback report when <see cref="RecommendedBitrateBps"/> changed. Never fires when
    /// congestion control is inactive. Fires on the RTP control thread — handlers must be fast.
    /// </summary>
    event Action? CongestionUpdated;
}
