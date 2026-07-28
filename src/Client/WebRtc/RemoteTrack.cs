namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// A remote media track surfaced by <see cref="IPeerConnection.TrackReceived"/> (the W3C per-track model).
/// Encoded frames arrive on <see cref="FrameReceived"/>.
/// </summary>
/// <remarks>
/// <see cref="StreamId"/> is the remote <c>a=msid</c> stream id (RFC 8830): tracks that share a stream id
/// belong to one remote MediaStream, so grouping by <see cref="StreamId"/> keeps a participant's audio and
/// video together (e.g. for a recording), while subscribing per track keeps them separable (e.g. routing
/// audio to a voice bot). See ADR-012.
/// </remarks>
public sealed class RemoteTrack
{
    internal RemoteTrack(TrackKind kind, string? streamId, string? trackId, string? mid)
    {
        Kind = kind;
        StreamId = streamId;
        TrackId = trackId;
        Mid = mid;
    }

    /// <summary>The media kind of this track.</summary>
    public TrackKind Kind { get; }

    /// <summary>
    /// The remote m-line's MID (<c>a=mid</c>, RFC 5888) this track was received on, or <see langword="null"/>
    /// when the remote advertised none (a legacy 1+1 offer). With several remote video tracks the MID
    /// distinguishes them (P2c).
    /// </summary>
    public string? Mid { get; }

    /// <summary>The remote MediaStream id (a=msid stream id), or <see langword="null"/> when the remote advertised none.</summary>
    public string? StreamId { get; }

    /// <summary>The remote per-track id (a=msid appdata), or <see langword="null"/> when the remote advertised none.</summary>
    public string? TrackId { get; }

    /// <summary>Raised with each encoded frame received on this track.</summary>
    public event EventHandler<EncodedFrame>? FrameReceived;

    internal void RaiseFrame(EncodedFrame frame) => FrameReceived?.Invoke(this, frame);
}
