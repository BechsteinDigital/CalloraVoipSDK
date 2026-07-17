namespace CalloraVoipSdk.Core.Infrastructure.WebRtc;

/// <summary>
/// A drain gate that lets an owner wait for in-flight operations to finish before tearing down a shared
/// resource. Operations wrap their work in <see cref="TryEnter"/>/<see cref="Exit"/>; the owner calls
/// <see cref="BeginDrainAsync"/> once, which refuses further entries and completes only after every
/// entered operation has exited. Used to close the WebRTC media Send-vs-Dispose race (HARD-C6): a send
/// holds the gate open so <c>DisposeAsync</c> cannot dispose the media session mid-send. Thread-safe.
/// </summary>
internal sealed class SendDrainGate
{
    private readonly object _sync = new();
    private bool _draining;
    private int _inFlight;
    private TaskCompletionSource? _drained;

    /// <summary>
    /// Registers one in-flight operation, returning <see langword="true"/> on success. Returns
    /// <see langword="false"/> once <see cref="BeginDrainAsync"/> has been called — the caller must not
    /// proceed. Pair a successful entry with exactly one <see cref="Exit"/> in a finally.
    /// </summary>
    public bool TryEnter()
    {
        lock (_sync)
        {
            if (_draining)
                return false;

            _inFlight++;
            return true;
        }
    }

    /// <summary>Marks one entered operation complete, releasing the drain when the last one exits.</summary>
    public void Exit()
    {
        lock (_sync)
        {
            if (--_inFlight == 0)
                _drained?.TrySetResult();
        }
    }

    /// <summary>
    /// Refuses further entries and returns a task that completes once all in-flight operations have
    /// exited (immediately when none are in flight). Idempotent — repeated calls return the same drain.
    /// </summary>
    public Task BeginDrainAsync()
    {
        lock (_sync)
        {
            _draining = true;
            if (_inFlight == 0)
                return Task.CompletedTask;

            return (_drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
    }
}
