namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// Handle to a running recording: owns the media-tap detach handle and completes the sink on stop. Stopping
/// is idempotent (only the first call detaches and completes).
/// </summary>
internal sealed class WebRtcRecording : IWebRtcRecording
{
    private readonly IDisposable _tapHandle;
    private readonly RecordingTap _tap;
    private readonly IEncodedMediaSink _sink;
    private int _stopped;

    public WebRtcRecording(IDisposable tapHandle, RecordingTap tap, IEncodedMediaSink sink)
    {
        _tapHandle = tapHandle;
        _tap = tap;
        _sink = sink;
    }

    public long FrameCount => _tap.FrameCount;

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        _tapHandle.Dispose();   // detach from the peer first, so no frame arrives after CompleteAsync
        await _sink.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
