namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// Records the encoded media flowing through a peer connection into an <see cref="IEncodedMediaSink"/> — an
/// L3 module built on <see cref="IPeerConnection.AttachMediaTap"/>. Transport-only: it captures the raw
/// codec bitstream per track and direction; muxing to a playable container is the sink's job.
/// </summary>
public interface IWebRtcRecorder
{
    /// <summary>
    /// Starts recording <paramref name="peer"/> into <paramref name="sink"/> (audio and video, both
    /// directions). Stop via the returned <see cref="IWebRtcRecording"/>.
    /// </summary>
    IWebRtcRecording Start(IPeerConnection peer, IEncodedMediaSink sink);
}
