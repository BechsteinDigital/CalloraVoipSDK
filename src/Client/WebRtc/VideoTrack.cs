namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// The public <see cref="IVideoTrack"/> handle for a video track added via
/// <see cref="IPeerConnection.AddVideoTrack()"/>. Holds the track's numeric MID and direction and routes
/// each frame through the owning <see cref="PeerConnection"/>'s send-lease path (so outbound media still
/// reaches attached media taps and the send never races session teardown). The MID is fixed at add time
/// from the offer's m-line layout, so a frame can be sent as soon as the transport is keyed.
/// </summary>
internal sealed class VideoTrack : IVideoTrack
{
    private readonly Func<ReadOnlyMemory<byte>, uint, CancellationToken, Task> _send;
    private readonly Func<string, ReadOnlyMemory<byte>, uint, CancellationToken, Task> _sendRid;

    public VideoTrack(
        string mid,
        TrackDirection direction,
        Func<ReadOnlyMemory<byte>, uint, CancellationToken, Task> send,
        Func<string, ReadOnlyMemory<byte>, uint, CancellationToken, Task> sendRid)
    {
        ArgumentException.ThrowIfNullOrEmpty(mid);
        ArgumentNullException.ThrowIfNull(send);
        ArgumentNullException.ThrowIfNull(sendRid);
        Mid = mid;
        Direction = direction;
        _send = send;
        _sendRid = sendRid;
    }

    /// <inheritdoc />
    public string Mid { get; }

    /// <inheritdoc />
    public TrackDirection Direction { get; }

    /// <inheritdoc />
    public Task SendFrameAsync(ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken cancellationToken = default)
        => _send(encodedFrame, rtpTimestamp, cancellationToken);

    /// <inheritdoc />
    public Task SendFrameAsync(string rid, ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rid);
        return _sendRid(rid, encodedFrame, rtpTimestamp, cancellationToken);
    }
}
