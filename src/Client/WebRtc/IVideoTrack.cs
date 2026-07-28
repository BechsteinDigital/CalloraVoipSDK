namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// A handle to an outbound video track added before the first offer with
/// <see cref="IPeerConnection.AddVideoTrack()"/> (its own <c>m=video</c> line on the shared BUNDLE
/// transport). Send encoded frames on this track with <see cref="SendFrameAsync(System.ReadOnlyMemory{byte}, uint, System.Threading.CancellationToken)"/>;
/// each track carries its own SSRC, so frames sent on distinct handles never collide (RFC 3550 §8.1).
/// </summary>
/// <remarks>
/// The multi-track surface is preview-grade: tracks must be added before <see cref="IPeerConnection.CreateOffer"/>
/// (mid-call add / renegotiation is a later package). Adding a track supplements the implicit primary video
/// track that <see cref="WebRtcConfiguration.EnableVideo"/> enables; the frameless
/// <see cref="IPeerConnection.SendVideoFrameAsync(System.ReadOnlyMemory{byte}, uint, System.Threading.CancellationToken)"/>
/// keeps addressing that primary track.
/// </remarks>
public interface IVideoTrack
{
    /// <summary>The track's negotiated MID (<c>a=mid</c>, RFC 5888) — a numeric token on the multi-track transport.</summary>
    string Mid { get; }

    /// <summary>The direction this track was added with (RFC 3264).</summary>
    TrackDirection Direction { get; }

    /// <summary>
    /// Packetises and sends one already-encoded video frame on this track. The app owns the codec
    /// (transport-only). Suppressed until the DTLS handshake keys the transport; a no-op when the negotiated
    /// directions do not carry outbound video on this track.
    /// </summary>
    /// <param name="encodedFrame">The already-encoded video frame payload.</param>
    /// <param name="rtpTimestamp">The frame's RTP timestamp on the track's 90 kHz clock.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    Task SendFrameAsync(ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken cancellationToken = default);

    /// <summary>
    /// Packetises and sends one already-encoded video frame on this track's simulcast <paramref name="rid"/>
    /// layer (RFC 8853). The layer must be one of the track's configured
    /// <see cref="VideoTrackOptions.SimulcastSendRids"/>; the app encodes each layer at its own
    /// resolution/bitrate and calls this once per layer per frame.
    /// </summary>
    /// <param name="rid">The simulcast layer id to send on.</param>
    /// <param name="encodedFrame">The already-encoded video frame payload for that layer.</param>
    /// <param name="rtpTimestamp">The frame's RTP timestamp on the track's 90 kHz clock.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    Task SendFrameAsync(string rid, ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken cancellationToken = default);
}
