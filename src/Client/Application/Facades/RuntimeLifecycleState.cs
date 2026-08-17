namespace CalloraVoipSdk;

/// <summary>
/// The started/stopping/stopped state of a client runtime, as a thread-safe three-state machine.
/// </summary>
/// <remarks>
/// A plain started flag cannot express "shutdown in progress", and clearing it up front made an aborted
/// shutdown unrecoverable (#166 P2-9): a cancelled host stop left the runtime marked as stopped while calls
/// were still up and lines still registered, and the retry returned immediately. Shutdown is therefore
/// claimed, then either completed or aborted — and an aborted shutdown returns to <c>Started</c>, so the next
/// attempt resumes it. Extracted from <see cref="VoipClient"/> so the transitions are unit-testable without a
/// live SIP transport.
/// </remarks>
internal sealed class RuntimeLifecycleState
{
    private const int Stopped = 0;
    private const int Started = 1;
    private const int Stopping = 2;

    private int _state;

    /// <summary>Whether the runtime is started and no shutdown has been claimed.</summary>
    internal bool IsStarted => Volatile.Read(ref _state) == Started;

    /// <summary>
    /// Claims the transition to started. Returns <see langword="false"/> when the runtime is already started
    /// or a shutdown is in flight — a start racing a shutdown must not overwrite the state the shutdown owns.
    /// </summary>
    internal bool TryStart() => Interlocked.CompareExchange(ref _state, Started, Stopped) == Stopped;

    /// <summary>
    /// Claims the shutdown. Returns <see langword="false"/> when the runtime never started, already stopped,
    /// or another caller owns an in-flight shutdown; only the caller that gets <see langword="true"/> runs the
    /// teardown and must finish it with <see cref="CompleteShutdown"/> or <see cref="AbortShutdown"/>.
    /// </summary>
    internal bool TryBeginShutdown() => Interlocked.CompareExchange(ref _state, Stopping, Started) == Started;

    /// <summary>Commits a finished shutdown: the runtime is stopped and can be started again.</summary>
    internal void CompleteShutdown() => Volatile.Write(ref _state, Stopped);

    /// <summary>
    /// Gives a claimed shutdown back: the runtime returns to started, so a later shutdown attempt resumes the
    /// teardown instead of finding a runtime that only looks stopped.
    /// </summary>
    internal void AbortShutdown() => Interlocked.CompareExchange(ref _state, Started, Stopping);
}
