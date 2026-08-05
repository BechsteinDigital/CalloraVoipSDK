namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// Thread-safe tracker of the peer connections opened through a client. The client tracks a peer when it is
/// created and the peer untracks itself when disposed, so <see cref="Active"/> always reflects the live set.
/// </summary>
internal sealed class PeerConnectionManager : IPeerConnectionManager
{
    private readonly object _sync = new();
    private readonly List<IPeerConnection> _peers = [];
    private bool _disposed;

    /// <inheritdoc />
    public IReadOnlyList<IPeerConnection> Active
    {
        get { lock (_sync) { return _peers.ToArray(); } }
    }

    /// <inheritdoc />
    public int Count
    {
        get { lock (_sync) { return _peers.Count; } }
    }

    /// <summary>
    /// Tracks a peer, or returns <see langword="false"/> when the owning client has begun disposal — so a peer
    /// created concurrently with (or after) <see cref="DrainForDispose"/> is never left registered in a dead
    /// owner (#166 P1-1). Atomic against <see cref="DrainForDispose"/> under the same lock.
    /// </summary>
    internal bool TryTrack(IPeerConnection peer)
    {
        lock (_sync)
        {
            if (_disposed)
                return false;
            _peers.Add(peer);
            return true;
        }
    }

    internal void Untrack(IPeerConnection peer)
    {
        lock (_sync) { _peers.Remove(peer); }
    }

    /// <summary>
    /// Marks the manager disposed (no further <see cref="TryTrack"/> succeeds) and returns the live set to tear
    /// down, atomically, so the dispose snapshot and the tracking gate cannot race (#166 P1-1).
    /// </summary>
    internal IReadOnlyList<IPeerConnection> DrainForDispose()
    {
        lock (_sync)
        {
            _disposed = true;
            return _peers.ToArray();
        }
    }
}
