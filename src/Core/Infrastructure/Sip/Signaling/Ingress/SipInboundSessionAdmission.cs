using System.Net;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// #158 P1-5 / #279 (atomic admission): decides whether one more dialog session may exist and reserves its
/// slot. A UAS creates dialog state for every served-user INVITE before any line/trunk takes ownership, so a
/// flood of INVITEs with distinct Call-IDs would otherwise pin unbounded state.
/// </summary>
/// <remarks>
/// The global ceiling is enforced by a compare-and-swap on a reservation counter rather than by reading the
/// session table's count: a count check sits apart from the insert, so concurrent INVITEs would all observe
/// the same free slot and admit past the ceiling (#279). The per-remote limiter additionally fair-shares that
/// budget across source addresses. A reservation is released exactly once — when the session is untracked, or
/// on any path between admission and insert that does not end in a tracked session.
/// </remarks>
internal sealed class SipInboundSessionAdmission
{
    /// <summary>
    /// Default cap on concurrent inbound dialog sessions (#158 P1-5).
    /// </summary>
    public const int DefaultMaxConcurrentSessions = 256;

    private readonly SipPerRemoteInboundSessionLimiter _perRemote;
    private int _reserved;

    /// <summary>
    /// Creates the admission control for one signaling service.
    /// </summary>
    /// <param name="maxConcurrentSessions">Global ceiling on tracked sessions; non-positive or null falls back
    /// to <see cref="DefaultMaxConcurrentSessions"/>.</param>
    /// <param name="maxPerRemote">Concurrent inbound sessions allowed per source IP; non-positive or null falls
    /// back to the limiter's own default.</param>
    public SipInboundSessionAdmission(int? maxConcurrentSessions = null, int? maxPerRemote = null)
    {
        MaxConcurrentSessions = maxConcurrentSessions is { } cap && cap > 0
            ? cap
            : DefaultMaxConcurrentSessions;
        _perRemote = new SipPerRemoteInboundSessionLimiter(maxPerRemote);
    }

    /// <summary>
    /// The global ceiling in force.
    /// </summary>
    public int MaxConcurrentSessions { get; }

    /// <summary>
    /// Currently reserved slots — one per tracked session, plus any admission still on its way to the table.
    /// </summary>
    internal int ReservedSlots => Volatile.Read(ref _reserved);

    /// <summary>
    /// Reserves one slot for an inbound session, honouring the global and the per-remote ceiling. On anything
    /// other than <see cref="SipInboundSessionAdmissionOutcome.Admitted"/> nothing is held and the caller must
    /// reject the request; on success the caller releases via <see cref="ReleaseInbound"/> unless the session
    /// becomes tracked.
    /// </summary>
    public SipInboundSessionAdmissionOutcome TryAdmitInbound(string callId, IPAddress remote)
    {
        ArgumentNullException.ThrowIfNull(callId);
        ArgumentNullException.ThrowIfNull(remote);

        if (!TryReserveSlot())
            return SipInboundSessionAdmissionOutcome.GlobalCapReached;

        if (!_perRemote.TryAdmit(callId, remote))
        {
            // Hand the global slot straight back — a refused admission must hold nothing.
            ReleaseSlot();
            return SipInboundSessionAdmissionOutcome.PerRemoteCapReached;
        }

        return SipInboundSessionAdmissionOutcome.Admitted;
    }

    /// <summary>
    /// Takes one slot unconditionally for an outbound session. An outgoing call is never refused by the inbound
    /// ceiling, but it occupies the same table and must count against it.
    /// </summary>
    public void ReserveOutbound() => Interlocked.Increment(ref _reserved);

    /// <summary>
    /// Releases the global slot and the per-remote reservation held for <paramref name="callId"/>. The
    /// per-remote release is a no-op for an unknown Call-ID (outbound sessions, or a second call for the same
    /// session), so this is safe on every teardown path.
    /// </summary>
    public void ReleaseInbound(string callId)
    {
        ReleaseSlot();
        _perRemote.Release(callId);
    }

    /// <summary>
    /// Releases one global slot without touching per-remote state (outbound insert failure).
    /// </summary>
    public void ReleaseSlot() => Interlocked.Decrement(ref _reserved);

    /// <summary>
    /// Drops all reservations (service disposal).
    /// </summary>
    public void Clear()
    {
        Interlocked.Exchange(ref _reserved, 0);
        _perRemote.Clear();
    }

    /// <summary>
    /// Current reserved session count for one remote (diagnostics/tests).
    /// </summary>
    internal int CountFor(IPAddress remote) => _perRemote.CountFor(remote);

    /// <summary>
    /// Claims one slot, or returns <see langword="false"/> at the ceiling. Compare-and-swap rather than
    /// increment-then-check: a transient overshoot would make a concurrent admission refuse against a count
    /// that was never real.
    /// </summary>
    private bool TryReserveSlot()
    {
        while (true)
        {
            var reserved = Volatile.Read(ref _reserved);
            if (reserved >= MaxConcurrentSessions)
                return false;
            if (Interlocked.CompareExchange(ref _reserved, reserved + 1, reserved) == reserved)
                return true;
        }
    }
}
