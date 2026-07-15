namespace CalloraVoipSdk.Core.Domain.Calls;

/// <summary>
/// One encoded video frame crossing the call boundary in either direction.
/// The SDK is transport-only: it moves already-encoded bytes and never encodes or
/// decodes — the application supplies the codec (VP8, H.264, …) and reads
/// <see cref="PayloadType"/> to identify it.
/// </summary>
/// <param name="Payload">The complete encoded video frame (one coded picture), codec-specific bytes.</param>
/// <param name="PayloadType">The negotiated RTP payload type identifying the video codec.</param>
/// <param name="RtpTimestamp">
/// The frame's RTP timestamp on the video clock (90 kHz). All RTP packets carrying this frame
/// share this value; it is absolute, not a duration (unlike <see cref="CallAudioFrame"/>).
/// </param>
/// <param name="IsKeyFrame">
/// <see langword="true"/> when the frame is (or is known to be) an intra-coded key frame that can be
/// decoded without prior frames; <see langword="false"/> when it is a delta frame or its type is not
/// known. Best-effort classification on the receive path; a send-side hint on the send path.
/// </param>
internal readonly record struct CallVideoFrame(
    byte[] Payload,
    int PayloadType,
    uint RtpTimestamp,
    bool IsKeyFrame);
