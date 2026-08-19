using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk;
using CalloraVoipSdk.Core.Application.Ports.Connectivity;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Stun.Client;
using CalloraVoipSdk.Core.Infrastructure.Turn.Client;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.WebRtc;

/// <summary>
/// Gathers server-reflexive (RFC 8445 §5.1.1) and relay (RFC 8656) ICE candidates for a
/// <see cref="WebRtcPeerConnection"/> through its pre-bound media socket, emitting each discovered candidate
/// via <paramref name="onCandidate"/> (RFC 8838 trickle). Extracted from the peer as a self-contained
/// collaborator (it holds no signalling state — the peer keeps ownership of the retained relay allocation and
/// its session) to keep the connection type under the 1000-line limit (ENGINEERING_RULES R3). Sequential per
/// server: each step runs its own temporary receive loop on the shared socket, so they must not overlap.
/// </summary>
internal sealed class WebRtcCandidateGatherer(
    IIceStunProbe? stunProbe,
    TurnAllocationProbe? turnProbe,
    ILogger logger)
{
    /// <summary>
    /// Reports a successful TURN allocation to the peer, which decides whether to retain it (first-wins) and
    /// returns the endpoint to advertise the relay candidate against (the mapped base, else the host base).
    /// </summary>
    /// <param name="serverEndPoint">The resolved TURN server transport address.</param>
    /// <param name="allocation">The TURN Allocate result (relayed endpoint, mapped base, credentials).</param>
    /// <param name="host">The bound local host endpoint (the relay's base fallback).</param>
    public delegate IPEndPoint? RelayGatheredCallback(
        IPEndPoint serverEndPoint, TurnAllocateResult allocation, IPEndPoint host);

    /// <summary>
    /// Asks a STUN server for this socket's reflexive address over an ALREADY RUNNING transport, when the
    /// receive loop owns the socket and a probe cannot read from it directly.
    /// </summary>
    /// <param name="server">The STUN server's transport address.</param>
    /// <param name="timeout">Per-attempt wait before retransmitting.</param>
    /// <param name="ct">Cancels the probe.</param>
    public delegate Task<IPEndPoint?> LiveReflexiveProbe(IPEndPoint server, TimeSpan timeout, CancellationToken ct);

    // Per-attempt wait for a live re-probe. Deliberately short: this runs alongside flowing media after an ICE
    // restart, so a slow or dead STUN server must cost a couple of retransmissions, not a visible stall.
    private static readonly TimeSpan LiveProbeTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Gathers srflx/relay candidates over <paramref name="socket"/> for the given <paramref name="servers"/>,
    /// emitting each via <paramref name="onCandidate"/>. A STUN server yields an srflx candidate (needs a STUN
    /// probe); a UDP TURN server yields a relay candidate when the allocation succeeds (needs a TURN probe),
    /// reported first to <paramref name="onRelayGathered"/> so the peer can retain/adopt it before the candidate
    /// is emitted. No-op for a server without a matching probe; a failed query is simply no candidate, never a throw.
    /// </summary>
    public async Task GatherAsync(
        IReadOnlyList<IceServerConfiguration> servers,
        IPEndPoint local,
        IPEndPoint relatedHost,
        Socket? socket,
        LiveReflexiveProbe? liveProbe,
        Action<SdpIceCandidate> onCandidate,
        RelayGatheredCallback onRelayGathered,
        CancellationToken ct)
    {
        foreach (var server in servers)
        {
            switch (server.Type)
            {
                case IceServerType.Stun when socket is null:
                    await ReGatherServerReflexiveAsync(server, local, relatedHost, liveProbe, onCandidate, ct).ConfigureAwait(false);
                    break;
                case IceServerType.Stun:
                    await GatherServerReflexiveAsync(server, local, relatedHost, socket, onCandidate, ct).ConfigureAwait(false);
                    break;
                case IceServerType.Turn when socket is null:
                    // A TURN allocation is keyed to the 5-tuple the transport still holds and is kept alive by
                    // its refresh loop, so the relay candidate did not change — re-allocating would only
                    // duplicate it. See ADR-072 for why the socket is deliberately preserved across a restart.
                    logger.LogDebug(
                        "Skipping TURN server {Host} on a live re-gather: the existing allocation still covers this 5-tuple.",
                        server.Host);
                    break;
                case IceServerType.Turn:
                    await GatherRelayAsync(server, local, relatedHost, socket, onCandidate, onRelayGathered, ct).ConfigureAwait(false);
                    break;
                default:
                    logger.LogDebug("Skipping ICE server {Host} of unsupported type {Type}.", server.Host, server.Type);
                    break;
            }
        }
    }

    // Queries one STUN server for the server-reflexive endpoint and emits an srflx candidate on success.
    // No-op without a STUN probe (a peer configured with STUN servers but no probe gathers host-only).
    // The live counterpart of GatherServerReflexiveAsync: same candidate, discovered over a transport whose
    // receive loop already owns the socket. Resolution is pinned to UDP because the media socket is UDP — a
    // TCP/TLS STUN server cannot describe this 5-tuple.
    private async Task ReGatherServerReflexiveAsync(
        IceServerConfiguration server,
        IPEndPoint local,
        IPEndPoint relatedHost,
        LiveReflexiveProbe? liveProbe,
        Action<SdpIceCandidate> onCandidate,
        CancellationToken ct)
    {
        if (liveProbe is null)
            return;

        IPEndPoint serverEndPoint;
        try
        {
            serverEndPoint = await StunIceProbe
                .ResolveUdpEndPointAsync(server, local.AddressFamily, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An unresolvable server is one fewer candidate, never a failed re-gather.
            logger.LogDebug(ex, "Could not resolve STUN server {Host} for a live re-gather.", server.Host);
            return;
        }

        var reflexive = await liveProbe(serverEndPoint, LiveProbeTimeout, ct).ConfigureAwait(false);
        if (reflexive is not null)
            onCandidate(WebRtcIceCandidateFactory.ServerReflexiveCandidate(reflexive, relatedHost));
    }

    private async Task GatherServerReflexiveAsync(
        IceServerConfiguration server,
        IPEndPoint local,
        IPEndPoint relatedHost,
        Socket socket,
        Action<SdpIceCandidate> onCandidate,
        CancellationToken ct)
    {
        if (stunProbe is null)
            return;

        var reflexive = await stunProbe
            .TryGetServerReflexiveEndPointAsync(local, server, socket, ct)
            .ConfigureAwait(false);
        if (reflexive is not null)
            onCandidate(WebRtcIceCandidateFactory.ServerReflexiveCandidate(reflexive, relatedHost));
    }

    // Allocates a TURN relay on the media socket and emits a relay candidate on success, reporting the
    // allocation to the peer first (retention/adoption). No-op without a TURN probe. Only UDP TURN is gathered
    // over the media socket — TCP/TLS TURN needs its own connection (a later slice) — and a failed allocation
    // is simply no relay candidate (as with a failed srflx query), never a throw.
    private async Task GatherRelayAsync(
        IceServerConfiguration server,
        IPEndPoint local,
        IPEndPoint relatedHost,
        Socket socket,
        Action<SdpIceCandidate> onCandidate,
        RelayGatheredCallback onRelayGathered,
        CancellationToken ct)
    {
        if (turnProbe is null)
        {
            logger.LogDebug(
                "Skipping TURN server {Host}: no TURN allocation probe is configured, so no relay candidate is gathered.",
                server.Host);
            return;
        }

        if (server.Transport != IceTransport.Udp)
        {
            // A TCP/TLS TURN entry is not gathered here (this path rides the UDP media socket); it is gathered as a
            // stream relay candidate over its own connection by the peer's stream relay path (ADR-073).
            logger.LogDebug(
                "TURN server {Host} with transport {Transport} is gathered as a stream relay over its own " +
                "connection (ADR-073), not on the media socket.",
                server.Host, server.Transport);
            return;
        }

        var serverEndPoint = await WebRtcTurnServerResolver.ResolveEndPointAsync(server, socket.AddressFamily, ct).ConfigureAwait(false);
        if (serverEndPoint is null)
        {
            logger.LogDebug(
                "Skipping TURN server {Host}: no address resolved in the media socket's family {Family}.",
                server.Host, socket.AddressFamily);
            return;
        }

        var allocation = await turnProbe
            .TryAllocateAsync(socket, serverEndPoint, WebRtcTurnServerResolver.BuildCredentials(server), lifetimeSeconds: null, ct)
            .ConfigureAwait(false);
        if (allocation is null)
            return;

        // The peer decides retention (first-wins) and adoption into an existing (answerer) session, and returns
        // the base to advertise raddr/rport against — the mapped (server-reflexive) base the server reported,
        // else the host base. Keeping that decision on the peer preserves the exact _gatheredRelay/_session
        // semantics; this gatherer only sequences the wire steps.
        var relatedBase = onRelayGathered(serverEndPoint, allocation, relatedHost);
        if (relatedBase is null)
        {
            // First-wins: this later TURN server's allocation was not retained/bound. Advertising a relay
            // candidate for it would let ICE nominate an unusable, unbound relay path (#155 P1-3). Tear the
            // surplus allocation down now with a Refresh(0) (#188) instead of letting it linger until its
            // lifetime expires and count against the server's quota; best-effort — a failed teardown still
            // expires on its own. Awaited while the gatherer still owns the socket, before media takes over.
            logger.LogDebug(
                "TURN server {Host}: allocation not retained (first-wins); tearing down the surplus allocation.",
                server.Host);
            await turnProbe.TryReleaseAsync(socket, serverEndPoint, allocation, ct).ConfigureAwait(false);
            return;
        }
        onCandidate(WebRtcIceCandidateFactory.RelayCandidate(allocation.RelayedEndPoint, relatedBase));
    }
}
