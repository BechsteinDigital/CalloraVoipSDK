namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// The built-in WebRTC recorder (L3 module). Attaches a media tap to a peer and streams every encoded frame
/// (audio and video, both directions) into an <see cref="IEncodedMediaSink"/>. Usable standalone
/// (<c>new WebRtcRecorder().Start(peer, sink)</c>) or registered as an <see cref="IWebRtcClientModule"/> and
/// resolved via <c>client.Modules.Get&lt;IWebRtcRecorder&gt;()</c>. Transport-only: it captures the raw
/// codec bitstream — turning it into a playable file is the sink's job.
/// </summary>
public sealed class WebRtcRecorder : IWebRtcRecorder, IWebRtcClientModule
{
    /// <inheritdoc />
    public string ModuleId => "callora.webrtc.recorder";

    /// <inheritdoc />
    public IWebRtcRecording Start(IPeerConnection peer, IEncodedMediaSink sink)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(sink);

        var tap = new RecordingTap(sink);
        var tapHandle = peer.AttachMediaTap(tap);
        return new WebRtcRecording(tapHandle, tap, sink);
    }
}
