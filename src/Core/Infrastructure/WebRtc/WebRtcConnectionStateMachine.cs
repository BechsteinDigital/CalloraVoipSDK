namespace CalloraVoipSdk.Core.Infrastructure.WebRtc;

/// <summary>
/// The transport half of a WebRTC peer's lifecycle (<see cref="WebRtcConnectionState"/>): the current state and
/// the one rule that governs every move — a transition to the state already held is a no-op, and
/// <see cref="WebRtcConnectionState.Closed"/> is terminal, so a late transport event can never resurrect a
/// disposed peer. Extracted from <see cref="WebRtcPeerConnection"/> to keep that file under the size limit.
/// </summary>
/// <remarks>
/// It shares the owning peer's gate rather than taking its own, so the state stays serialised against the peer's
/// other guarded fields exactly as it was when it lived inline. The change event is raised <em>outside</em> the
/// gate (K3): a handler may re-enter the peer, and one that throws must not break the transport path — the
/// supplied raise delegate owns that policy.
/// </remarks>
internal sealed class WebRtcConnectionStateMachine
{
    private readonly object _gate;
    private readonly Action<WebRtcConnectionState> _raise;
    private WebRtcConnectionState _state = WebRtcConnectionState.New;

    /// <param name="gate">The owning peer's lock, shared so the state keeps its original serialisation.</param>
    /// <param name="raise">Fires the peer's state-change event; invoked outside <paramref name="gate"/>.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public WebRtcConnectionStateMachine(object gate, Action<WebRtcConnectionState> raise)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _raise = raise ?? throw new ArgumentNullException(nameof(raise));
    }

    /// <summary>The current connection state.</summary>
    public WebRtcConnectionState Current
    {
        get { lock (_gate) { return _state; } }
    }

    /// <summary>
    /// Moves to <paramref name="next"/> and raises the change event, unless that is already the current state or
    /// the peer is closed. Callers must NOT hold the shared gate.
    /// </summary>
    /// <param name="next">The state to move to.</param>
    public void TransitionTo(WebRtcConnectionState next)
    {
        lock (_gate)
        {
            if (_state == next || _state == WebRtcConnectionState.Closed)
                return;
            _state = next;
        }

        _raise(next);
    }
}
