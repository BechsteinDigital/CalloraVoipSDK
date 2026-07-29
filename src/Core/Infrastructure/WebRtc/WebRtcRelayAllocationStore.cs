using System;
using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Common.Relay;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Turn.Client;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.WebRtc;

/// <summary>
/// Owns the peer's gathered TURN relay allocation (RFC 8656), factored out of <see cref="WebRtcPeerConnection"/>
/// to keep that file within the size limit (mirroring the existing collaborator split — gathering, SDP options,
/// candidate emission, the session-event bridge, the congestion relay). It retains the first successful
/// allocation and its TURN server, keyed to the media socket's 5-tuple, which survives the hand-over to the
/// transport, so the relay coordinator can adopt it post-Start without re-allocating.
/// <para>
/// The first-wins latch and the "is there already a session to adopt into?" decision are taken atomically under
/// this store's own lock: <see cref="OnGathered"/> reads the caller's session snapshot inside the same critical
/// section that latches, so a concurrent gather and a session build cannot interleave into a lost adoption. The
/// caller's snapshot delegate takes the peer's own lock; that nesting is one-directional (the peer never calls
/// in while holding its lock), so no lock-order inversion arises.
/// </para>
/// </summary>
internal sealed class WebRtcRelayAllocationStore
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly object _sync = new();
    private (IPEndPoint ServerEndPoint, TurnAllocateResult Allocation)? _gathered;

    /// <summary>
    /// Creates the store over the logger factory used to build the relay ICE binding when an allocation is
    /// adopted into an already-built (answerer) session.
    /// </summary>
    /// <param name="loggerFactory">Builds the adopted relay binding's loggers.</param>
    public WebRtcRelayAllocationStore(ILoggerFactory loggerFactory)
        => _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));

    /// <summary>
    /// The retained allocation (its TURN server endpoint and the allocation — relayed endpoint, lifetime,
    /// effective realm/nonce credentials), or <see langword="null"/> when none was gathered.
    /// </summary>
    public (IPEndPoint ServerEndPoint, TurnAllocateResult Allocation)? Snapshot
    {
        get { lock (_sync) { return _gathered; } }
    }

    /// <summary>
    /// The relay ICE binding factory for the retained allocation, for the offerer to hand to its session build so
    /// the relay data path rides the socket the transport takes over; <see langword="null"/> when no allocation
    /// was gathered (a non-relay offer). The answerer adopts its later-gathered allocation via <see cref="OnGathered"/>.
    /// </summary>
    public RelayIceBindingFactory? BuildOfferFactory()
        => Snapshot is { } relay
            ? WebRtcRelayBinding.CreateFactory(relay.ServerEndPoint, relay.Allocation, _loggerFactory)
            : null;

    /// <summary>
    /// Records a freshly gathered relay allocation, first-wins: a later success does not replace the retained
    /// one. When THIS allocation is the one retained AND the caller's session snapshot already yields a session —
    /// the answerer, which built its session (direct-only, no gathered allocation yet) before gathering — the
    /// relay candidate is adopted into it now (idempotent). The offerer gathers before applying the answer, so
    /// its session does not exist yet here and wires the relay at construction from the options factory instead.
    /// The adopt runs outside this store's lock (it takes the session's own gate and needs none of our state).
    /// </summary>
    /// <param name="serverEndPoint">The TURN server the allocation was made against.</param>
    /// <param name="allocation">The gathered allocation.</param>
    /// <param name="local">The host base to fall back to when the server reported no mapped address.</param>
    /// <param name="sessionSnapshot">Snapshots the peer's current media session (adopt target), or null.</param>
    /// <returns>The raddr/rport base for the relay candidate: the mapped (server-reflexive) base, else the host base.</returns>
    public IPEndPoint OnGathered(
        IPEndPoint serverEndPoint,
        TurnAllocateResult allocation,
        IPEndPoint local,
        Func<BundledMediaSession?> sessionSnapshot)
    {
        ArgumentNullException.ThrowIfNull(serverEndPoint);
        ArgumentNullException.ThrowIfNull(allocation);
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(sessionSnapshot);

        BundledMediaSession? adoptInto = null;
        lock (_sync)
        {
            if (_gathered is null)
            {
                _gathered = (serverEndPoint, allocation);
                // Read the adopt target inside the latch so a concurrent build cannot slip between latch and read.
                adoptInto = sessionSnapshot();
            }
        }

        // Adopt outside the lock: AdoptRelay builds the TURN control stack and takes the ICE driver's own gate.
        // AdoptRelay is idempotent, so a session that already wired a relay (it should not on the answerer, but
        // defensively) is unaffected.
        adoptInto?.AdoptRelay(WebRtcRelayBinding.CreateFactory(serverEndPoint, allocation, _loggerFactory));

        return allocation.MappedEndPoint ?? local;
    }
}
