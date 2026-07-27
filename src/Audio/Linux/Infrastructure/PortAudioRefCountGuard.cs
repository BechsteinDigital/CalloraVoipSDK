namespace CalloraVoipSdk.Audio.Linux;

/// <summary>
/// Reference-counted gate around a paired initialize/terminate lifecycle. PortAudio's
/// <c>Pa_Initialize</c>/<c>Pa_Terminate</c> are themselves reference-counted at the native layer,
/// but the previous device code called <c>Initialize</c> from the constructor and from every static
/// and instance enumeration helper while only ever calling <c>Terminate</c> once, from
/// <c>Dispose</c> — leaving the native refcount permanently above zero and the library initialized
/// for the life of the process (issue #18, A7). This guard makes each acquire pair with exactly one
/// release: the underlying <c>initialize</c> runs on the 0→1 transition and <c>terminate</c> on the
/// 1→0 transition, so the count returns to zero. The initialize/terminate actions are injected so
/// the counting logic can be unit-tested without a real audio backend.
/// </summary>
public sealed class PortAudioRefCountGuard
{
    private readonly object _sync = new();
    private readonly Action _initialize;
    private readonly Action _terminate;
    private int _count;

    /// <summary>
    /// Creates a guard over the supplied initialize/terminate actions.
    /// </summary>
    /// <param name="initialize">Invoked on the 0→1 acquire transition.</param>
    /// <param name="terminate">Invoked on the 1→0 release transition.</param>
    public PortAudioRefCountGuard(Action initialize, Action terminate)
    {
        ArgumentNullException.ThrowIfNull(initialize);
        ArgumentNullException.ThrowIfNull(terminate);
        _initialize = initialize;
        _terminate = terminate;
    }

    /// <summary>
    /// Current number of outstanding acquisitions. Zero means the backend is terminated.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _count;
            }
        }
    }

    /// <summary>
    /// Acquires the backend, initializing it on the first outstanding acquisition. Balance every
    /// call with exactly one <see cref="Release"/>.
    /// </summary>
    public void Acquire()
    {
        lock (_sync)
        {
            if (_count == 0)
                _initialize();

            _count++;
        }
    }

    /// <summary>
    /// Releases one acquisition, terminating the backend once the last outstanding acquisition is
    /// released. Releasing without a matching acquire is a no-op.
    /// </summary>
    public void Release()
    {
        lock (_sync)
        {
            if (_count == 0)
                return;

            _count--;
            if (_count == 0)
                _terminate();
        }
    }
}
