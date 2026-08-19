using System.Net;

namespace CalloraVoipSdk.Core.Infrastructure.Stun.Ice;

/// <summary>
/// A local ICE candidate the controlling driver pairs against remote candidates (RFC 8445 §5.1.1). A local
/// candidate is characterised by its <em>send path</em> — the <see cref="Check"/> delegate that sends one
/// connectivity check to a remote target over this candidate's transport. Host and server-reflexive share
/// the direct send path (srflx is only the mapped view of the same socket); a relay candidate carries its
/// own path (framed through a TURN server), which is what makes more than one local candidate meaningful.
/// </summary>
internal sealed class IceLocalCandidate
{
    /// <summary>Candidate type token (host, srflx, relay) — orders pair priority and routes nomination.</summary>
    public required string Type { get; init; }

    /// <summary>The local candidate priority (RFC 8445 §5.1.2.1), an input to the pair priority.</summary>
    public required long Priority { get; init; }

    /// <summary>
    /// Sends one connectivity check to a remote target over this local candidate's transport —
    /// <c>(remoteTarget, useCandidate, ct)</c> — returning <see langword="true"/> when a matching response
    /// arrives. For host/srflx this sends directly on the media socket; for relay it frames the check through
    /// the TURN server.
    /// </summary>
    public required Func<IPEndPoint, bool, CancellationToken, Task<bool>> Check { get; init; }

    /// <summary>
    /// The relay send path this candidate's consent freshness rides once nominated (a TURN Send indication),
    /// or <see langword="null"/> for a direct (host/srflx) candidate that sends on the media socket. Carried on
    /// the candidate so nomination routes consent through the candidate that actually won — which distinguishes
    /// coexisting relays (for example a UDP relay and a stream relay) rather than reading a single shared field.
    /// </summary>
    public Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask>? SendVia { get; init; }

    /// <summary>
    /// Invoked once, when THIS candidate is the nominated pair's local (RFC 8445 §8), with the nominated remote.
    /// A relay candidate that owns its own transport (a stream relay, ADR-073) uses it to switch that transport
    /// onto the relay data path. Left <see langword="null"/> by direct candidates and by the UDP relay, which
    /// switches its shared socket in place through the session-level relay-nominated callback instead.
    /// </summary>
    public Action<IPEndPoint>? OnNominated { get; init; }
}
