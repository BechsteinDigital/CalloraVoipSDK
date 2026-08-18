namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// Deeper control for <see cref="IPeerConnection.AddVideoTrack(VideoTrackOptions)"/>: the track's
/// direction, its codec preferences, send-side simulcast layers, and the MediaStream it belongs to. Every
/// field is optional — <c>new VideoTrackOptions()</c> yields a send-recv track using the client's configured
/// <see cref="WebRtcConfiguration.VideoCodecs"/>, matching the parameterless
/// <see cref="IPeerConnection.AddVideoTrack()"/> happy path.
/// </summary>
/// <remarks>
/// Opaque (end-to-end encrypted) frames are deliberately <em>not</em> a per-track option: the switch is
/// <see cref="WebRtcConfiguration.OpaqueVideoFrames"/> and applies to the whole peer, including tracks added
/// here at runtime (#223, ADR-068). SDP carries no per-m-line attribute for it, so a per-track choice would need
/// a policy channel of its own through the session factory and the renegotiator — and the requirement it serves
/// covers the entire session, not one stream.
/// </remarks>
public sealed class VideoTrackOptions
{
    /// <summary>The negotiated direction of the track's m-line (RFC 3264). Defaults to <see cref="TrackDirection.SendRecv"/>.</summary>
    public TrackDirection Direction { get; init; } = TrackDirection.SendRecv;

    /// <summary>
    /// Video codecs to offer on this track, by name (<c>H264</c>, <c>VP8</c>). <see langword="null"/> (the
    /// default) uses the client's configured <see cref="WebRtcConfiguration.VideoCodecs"/>. Unknown names are
    /// rejected when the track is added.
    /// </summary>
    public IReadOnlyList<string>? Codecs { get; init; }

    /// <summary>
    /// Send-side simulcast layer ids for this track (RFC 8853), advertised as <c>a=rid … send</c> plus
    /// <c>a=simulcast:send …</c>. Empty (the default) offers a single video stream; send per layer with
    /// <see cref="IVideoTrack.SendFrameAsync(string, System.ReadOnlyMemory{byte}, uint, System.Threading.CancellationToken)"/>.
    /// </summary>
    public IReadOnlyList<string> SimulcastSendRids { get; init; } = [];

    /// <summary>
    /// Receive-side simulcast layer ids to ask the peer for on this track (RFC 8853 §5.3), advertised as
    /// <c>a=rid … recv</c> plus <c>a=simulcast:recv …</c>. Empty (the default) asks for a single stream. An
    /// answerer may only simulcast what the offer marked recv, so a receive-only peer (e.g. a conference host)
    /// sets this to receive the peer's layers addressably; each arriving layer carries its rid on the received
    /// frame. This peer must be the offerer for the request to be advertised.
    /// </summary>
    public IReadOnlyList<string> SimulcastRecvRids { get; init; } = [];

    /// <summary>
    /// The WebRTC MediaStream id this track belongs to (<c>a=msid</c> stream id, RFC 8830).
    /// <see langword="null"/> (the default) puts the track in the peer's default stream. Tracks that share a
    /// stream id are grouped as one remote MediaStream on the receiver.
    /// </summary>
    public string? StreamId { get; init; }
}
