namespace CalloraVoipSdk.Core.Application.Media;

/// <summary>
/// One encoded video frame on the public media contract. The SDK is transport-only: the payload
/// carries already-encoded codec bytes (one coded picture) as negotiated for the call — it is not
/// decoded pixels, and the SDK never encodes or decodes. Bring your own codec (VP8, H.264, …) and
/// read <see cref="PayloadType"/> to identify it.
/// </summary>
/// <param name="Payload">Encoded video frame bytes (one coded picture).</param>
/// <param name="PayloadType">Negotiated RTP payload type identifying the video codec.</param>
/// <param name="RtpTimestamp">
/// The frame's RTP timestamp on the video clock (90 kHz). All RTP packets carrying this frame share
/// this value; it is absolute, not a duration.
/// </param>
/// <param name="IsKeyFrame">
/// <see langword="true"/> when the frame is (or is known to be) an intra-coded key frame decodable
/// without prior frames; <see langword="false"/> for a delta frame or when the type is not known.
/// </param>
public readonly record struct VideoFrame(
    ReadOnlyMemory<byte> Payload,
    int PayloadType,
    uint RtpTimestamp,
    bool IsKeyFrame);
