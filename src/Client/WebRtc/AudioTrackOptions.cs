namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// Deeper control for <see cref="IPeerConnection.AddAudioTrack(AudioTrackOptions)"/>: the track's direction,
/// its codec preferences, and the MediaStream it belongs to. Every field is optional —
/// <c>new AudioTrackOptions()</c> yields a send-recv track using the client's configured
/// <see cref="WebRtcConfiguration.AudioCodecs"/>, matching the parameterless
/// <see cref="IPeerConnection.AddAudioTrack()"/> happy path. Audio has no simulcast, so there is no
/// per-layer/rid surface here.
/// </summary>
public sealed class AudioTrackOptions
{
    /// <summary>The negotiated direction of the track's m-line (RFC 3264). Defaults to <see cref="TrackDirection.SendRecv"/>.</summary>
    public TrackDirection Direction { get; init; } = TrackDirection.SendRecv;

    /// <summary>
    /// Audio codecs to offer on this track, by name (<c>opus</c>, <c>PCMU</c>, <c>PCMA</c>, <c>G722</c>).
    /// <see langword="null"/> (the default) uses the client's configured
    /// <see cref="WebRtcConfiguration.AudioCodecs"/>. Unknown names are rejected when the track is added.
    /// </summary>
    public IReadOnlyList<string>? Codecs { get; init; }

    /// <summary>
    /// The WebRTC MediaStream id this track belongs to (<c>a=msid</c> stream id, RFC 8830).
    /// <see langword="null"/> (the default) puts the track in the peer's default stream. Tracks that share a
    /// stream id are grouped as one remote MediaStream on the receiver.
    /// </summary>
    public string? StreamId { get; init; }
}
