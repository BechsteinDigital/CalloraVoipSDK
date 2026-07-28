namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// Deeper control for <see cref="IPeerConnection.AddVideoTrack(VideoTrackOptions)"/>: the track's
/// direction, its codec preferences, send-side simulcast layers, and the MediaStream it belongs to. Every
/// field is optional — <c>new VideoTrackOptions()</c> yields a send-recv track using the client's configured
/// <see cref="WebRtcConfiguration.VideoCodecs"/>, matching the parameterless
/// <see cref="IPeerConnection.AddVideoTrack()"/> happy path.
/// </summary>
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
    /// The WebRTC MediaStream id this track belongs to (<c>a=msid</c> stream id, RFC 8830).
    /// <see langword="null"/> (the default) puts the track in the peer's default stream. Tracks that share a
    /// stream id are grouped as one remote MediaStream on the receiver.
    /// </summary>
    public string? StreamId { get; init; }
}
