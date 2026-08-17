namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// Handle to a running recording: owns the media-tap detach handle and completes the sink on stop. Stopping
/// is modelled as ONE shared operation — the first caller runs the flush, every concurrent or later caller
/// joins that same flush (#166 P2-5). The sink is completed exactly once on the success path.
/// </summary>
internal sealed class WebRtcRecording : IWebRtcRecording
{
    private readonly IDisposable _tapHandle;
    private readonly RecordingTap _tap;
    private readonly IEncodedMediaSink _sink;
    // Guards the shared stop operation. Held only while publishing/clearing the field — never across the flush.
    private readonly object _sync = new();
    private Task? _stopping;

    public WebRtcRecording(IDisposable tapHandle, RecordingTap tap, IEncodedMediaSink sink)
    {
        _tapHandle = tapHandle;
        _tap = tap;
        _sink = sink;
    }

    public long FrameCount => _tap.FrameCount;

    /// <summary>
    /// Detaches the tap and completes the sink. Concurrent callers join the in-flight stop and only return
    /// once the flush has actually finished — a second stop never reports success while the first is still
    /// flushing. A failed or cancelled flush surfaces to every joined caller and leaves the recording
    /// stoppable, so a later stop (or <see cref="DisposeAsync"/>) re-runs the flush instead of being a
    /// permanent silent no-op.
    /// </summary>
    /// <remarks>
    /// The first caller's <paramref name="cancellationToken"/> governs the shared flush; a joining caller's
    /// token cannot cancel work already running on behalf of someone else.
    /// </remarks>
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        // Detach from the peer first, so no frame arrives while the sink is being completed. The handle is
        // idempotent, so this is safe outside the lock, on a joining caller and on a retry after a failed flush.
        _tapHandle.Dispose();

        TaskCompletionSource? owned = null;
        Task stopping;
        lock (_sync)
        {
            // The completion source is published BEFORE the flush starts, so a caller that arrives while the
            // flush is still running always finds the shared operation and joins it.
            if (_stopping is null)
            {
                owned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _stopping = owned.Task;
            }

            stopping = _stopping;
        }

        return owned is null ? stopping : FlushAsync(owned, cancellationToken);
    }

    private async Task FlushAsync(TaskCompletionSource completion, CancellationToken cancellationToken)
    {
        try
        {
            await _sink.CompleteAsync(cancellationToken).ConfigureAwait(false);
            completion.SetResult();
        }
        catch (Exception ex)
        {
            // Keep the recording stoppable: a failed flush must not latch the handle into a "stopped" state
            // in which every later Stop/Dispose silently no-ops and the buffered media is never written.
            lock (_sync)
            {
                _stopping = null;
            }

            completion.SetException(ex);
            // Joined callers observe the fault through their await; an unjoined shared task would otherwise
            // surface as an unobserved task exception, so mark it observed here and rethrow to this caller.
            _ = completion.Task.Exception;
            throw;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
