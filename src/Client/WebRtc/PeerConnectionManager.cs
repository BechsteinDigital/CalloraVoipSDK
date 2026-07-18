namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// Thread-safe tracker of the peer connections opened through a client. The client tracks a peer when it is
/// created and the peer untracks itself when disposed, so <see cref="Active"/> always reflects the live set.
/// </summary>
internal sealed class PeerConnectionManager : IPeerConnectionManager
{
    private readonly object _sync = new();
    private readonly List<IPeerConnection> _peers = [];

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

    internal void Track(IPeerConnection peer)
    {
        lock (_sync) { _peers.Add(peer); }
    }

    internal void Untrack(IPeerConnection peer)
    {
        lock (_sync) { _peers.Remove(peer); }
    }
}
