using CalloraVoipSdk.Core.Infrastructure.Rtp;

namespace CalloraVoipSdk.Core.Infrastructure.WebRtc;

/// <summary>
/// Runs one WebRTC media send or RTCP feedback request under a <see cref="SendDrainGate"/> drain lease
/// (HARD-C6): the lease keeps <c>WebRtcPeerConnection.DisposeAsync</c> from tearing the media session down
/// mid-send, and a send begun after dispose is refused. Extracted from <see cref="WebRtcPeerConnection"/> so that
/// peer stays under the 1000-line rule; the behaviour is byte-identical to the former inline lease/snapshot/Exit
/// bodies.
/// </summary>
/// <remarks>
/// This collaborator is deliberately <em>lock-free</em>: it never touches the peer's <c>_sync</c> gate. The
/// live-session snapshot (which IS <c>_sync</c>-guarded) is read behind the <paramref name="sessionSnapshot"/>
/// delegate the peer supplies, so the peer keeps sole ownership of its guarded state and this class only sequences
/// the gate-enter → invoke → gate-exit protocol. That keeps the critical-section boundary inside the peer (no lock
/// split at the media core), while the repetitive lease plumbing lives here.
/// </remarks>
internal sealed class WebRtcSendLease
{
    private readonly SendDrainGate _sendGate;
    private readonly Func<BundledMediaSession?> _sessionSnapshot;

    /// <summary>Creates the lease runner over the peer's drain gate and its live-session snapshot delegate.</summary>
    /// <param name="sendGate">The peer's send-vs-dispose drain gate (HARD-C6).</param>
    /// <param name="sessionSnapshot">Reads the peer's current media session under its own <c>_sync</c> lock.</param>
    public WebRtcSendLease(SendDrainGate sendGate, Func<BundledMediaSession?> sessionSnapshot)
    {
        _sendGate = sendGate ?? throw new ArgumentNullException(nameof(sendGate));
        _sessionSnapshot = sessionSnapshot ?? throw new ArgumentNullException(nameof(sessionSnapshot));
    }

    /// <summary>
    /// Runs one send under a drain lease: the lease keeps <c>DisposeAsync</c> from tearing down the session
    /// mid-send (HARD-C6), and a send begun after dispose is refused (<see cref="AcquireSendLease"/> throws
    /// <see cref="ObjectDisposedException"/>).
    /// </summary>
    /// <param name="send">The send to run against the live session.</param>
    /// <exception cref="ObjectDisposedException">The peer is disposing or disposed.</exception>
    /// <exception cref="InvalidOperationException">No BUNDLE media session was built.</exception>
    public async Task SendViaLeaseAsync(Func<BundledMediaSession, Task> send)
    {
        var session = AcquireSendLease();
        try
        {
            await send(session).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Exit();
        }
    }

    /// <summary>
    /// Shared gate/snapshot boilerplate for the key-frame overloads: takes a drain lease so the RTCP send never
    /// races session teardown, snapshots the live session, and delegates the actual PLI to
    /// <paramref name="request"/>. A no-op returning <see langword="false"/> when the peer is draining/disposed or
    /// no session exists yet — byte-identical gate/snapshot/Exit semantics to the pre-extraction inline bodies.
    /// </summary>
    /// <param name="request">Issues the actual key-frame request against the live session.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async ValueTask<bool> RequestKeyFrameCoreAsync(
        Func<BundledMediaSession, CancellationToken, ValueTask<bool>> request, CancellationToken cancellationToken)
    {
        if (!_sendGate.TryEnter())
            return false;
        try
        {
            var session = _sessionSnapshot();
            return session is not null
                && await request(session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Exit();
        }
    }

    // Takes a drain lease for one send and returns the live session. The lease keeps DisposeAsync from disposing
    // the session until the send's Exit; a send begun after dispose is refused. Callers MUST Exit the gate (the
    // send methods do so in a finally) once the returned session is no longer used.
    private BundledMediaSession AcquireSendLease()
    {
        if (!_sendGate.TryEnter())
            throw new ObjectDisposedException(nameof(WebRtcPeerConnection));

        var session = _sessionSnapshot();
        if (session is null)
        {
            _sendGate.Exit();
            throw new InvalidOperationException("Apply a BUNDLE remote description before exchanging media.");
        }

        return session;
    }
}
