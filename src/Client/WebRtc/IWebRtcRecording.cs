namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// A running recording of a peer connection. Stop it with <see cref="StopAsync"/> (or by disposing) to
/// detach from the peer and flush the sink.
/// </summary>
public interface IWebRtcRecording : IAsyncDisposable
{
    /// <summary>The number of frames captured so far.</summary>
    long FrameCount { get; }

    /// <summary>Stops recording: detaches from the peer and completes the sink. Idempotent.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
