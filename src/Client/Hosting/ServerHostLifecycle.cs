namespace CalloraVoipSdk.Hosting;

/// <summary>
/// The start/dispose lifecycle shared by the server-hosting facades (<see cref="StunServerHost"/>,
/// <see cref="TurnServerHost"/>).
/// </summary>
/// <remarks>
/// Both hosts used to commit their started flag BEFORE running the server's start, so a start that threw left
/// the host permanently marked as started and every retry was a silent no-op — a server that reports itself as
/// serving while nothing listens (#166 P3-12). Here the flag is only committed once the start actually
/// returned, and disposal is a real state rather than a second flag: starting a disposed host is refused
/// instead of quietly doing nothing. The lock is held across the start call — this is a hosting lifecycle
/// operation, not a media path — so a concurrent start and dispose cannot interleave.
/// </remarks>
internal sealed class ServerHostLifecycle
{
    private readonly object _sync = new();
    private bool _started;
    private bool _disposed;

    /// <summary>Whether the server has been started and the start returned successfully.</summary>
    internal bool IsStarted
    {
        get { lock (_sync) return _started; }
    }

    /// <summary>
    /// Runs <paramref name="start"/> at most once. A failing start is not committed, so the host stays
    /// startable and a retry runs it again.
    /// </summary>
    /// <param name="start">The server's start action.</param>
    /// <param name="owner">The host instance, for the <see cref="ObjectDisposedException"/> it may throw.</param>
    /// <exception cref="ObjectDisposedException">The host has been disposed.</exception>
    internal void Start(Action start, object owner)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, owner);
            if (_started)
            {
                return;
            }

            start();
            _started = true;
        }
    }

    /// <summary>
    /// Claims disposal for exactly one caller, which then performs the asynchronous teardown outside the lock.
    /// Returns <see langword="false"/> for every later call, so the teardown runs once.
    /// </summary>
    internal bool TryBeginDispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return false;
            }

            _disposed = true;
            return true;
        }
    }
}
