namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// The public <see cref="IAudioTrack"/> handle for an audio track added via
/// <see cref="IPeerConnection.AddAudioTrack()"/>. Holds the track's numeric MID and direction and routes each
/// payload through the owning <see cref="PeerConnection"/>'s send-lease path (so outbound media still reaches
/// attached media taps and the send never races session teardown). The MID is fixed at add time from the offer's
/// m-line layout, so a payload can be sent as soon as the transport is keyed.
/// </summary>
internal sealed class AudioTrack : IAudioTrack
{
    private readonly Func<ReadOnlyMemory<byte>, uint, CancellationToken, Task> _send;

    public AudioTrack(
        string mid,
        TrackDirection direction,
        Func<ReadOnlyMemory<byte>, uint, CancellationToken, Task> send)
    {
        ArgumentException.ThrowIfNullOrEmpty(mid);
        ArgumentNullException.ThrowIfNull(send);
        Mid = mid;
        Direction = direction;
        _send = send;
    }

    /// <inheritdoc />
    public string Mid { get; }

    /// <inheritdoc />
    public TrackDirection Direction { get; }

    /// <inheritdoc />
    public Task SendFrameAsync(ReadOnlyMemory<byte> encodedAudioFrame, uint rtpTimestamp, CancellationToken cancellationToken = default)
        => _send(encodedAudioFrame, rtpTimestamp, cancellationToken);
}
