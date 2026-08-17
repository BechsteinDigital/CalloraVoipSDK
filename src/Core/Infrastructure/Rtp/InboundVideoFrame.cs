namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// One reassembled inbound video frame together with the per-frame facts that travel with it up the receive
/// path: its RTP timestamp, whether it is a key frame, and — when the peer negotiated the Dependency
/// Descriptor (#225) — the layer the sender put it on.
/// </summary>
/// <remarks>
/// <para>
/// A parameter object rather than four more positional arguments: the frame crosses three event hops
/// (track → session → peer) before it reaches the SDK surface, and every fact the descriptor adds would
/// otherwise widen all of them. What routes a frame (MID, RID) stays a separate argument on those events —
/// those are addresses, not properties of the frame.
/// </para>
/// <para>
/// A struct on purpose: one per inbound video frame on the media hot path, which allocates nothing it can
/// avoid (K3). <see cref="Payload"/> is the reassembled frame buffer the depacketiser produced; ownership
/// passes to the subscriber for the duration of the call.
/// </para>
/// </remarks>
/// <param name="Payload">The reassembled encoded frame.</param>
/// <param name="RtpTimestamp">The frame's RTP timestamp (RFC 3550 §5.1).</param>
/// <param name="IsKeyFrame">
/// Whether the frame can be decoded on its own — from the Dependency Descriptor where one was negotiated and
/// present, otherwise derived from the payload by the depacketiser. <paramref name="KeyFrameSource"/> says
/// which, because the two do not survive end-to-end encryption equally well.
/// </param>
/// <param name="KeyFrameSource">Where <paramref name="IsKeyFrame"/> came from (#310).</param>
/// <param name="SpatialId">
/// The frame's spatial layer from the Dependency Descriptor, or <see langword="null"/> when no descriptor was
/// negotiated, none rode on the frame, or its template could not be resolved (a stream joined mid-sequence).
/// </param>
/// <param name="TemporalId">The frame's temporal layer, resolved the same way as <paramref name="SpatialId"/>.</param>
internal readonly record struct InboundVideoFrame(
    byte[] Payload,
    uint RtpTimestamp,
    bool IsKeyFrame,
    int? SpatialId,
    int? TemporalId,
    VideoKeyFrameSource KeyFrameSource);
