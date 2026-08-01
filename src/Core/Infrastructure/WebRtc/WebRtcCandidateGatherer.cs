using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk;
using CalloraVoipSdk.Core.Application.Ports.Connectivity;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
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
    public delegate IPEndPoint RelayGatheredCallback(
        IPEndPoint serverEndPoint, TurnAllocateResult allocation, IPEndPoint host);

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
        Socket socket,
        Action<SdpIceCandidate> onCandidate,
        RelayGatheredCallback onRelayGathered,
        CancellationToken ct)
    {
        foreach (var server in servers)
        {
            switch (server.Type)
            {
                case IceServerType.Stun:
                    await GatherServerReflexiveAsync(server, local, relatedHost, socket, onCandidate, ct).ConfigureAwait(false);
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
            // Loud enough to diagnose the config trap on the non-builder path (a WebRtcConfiguration set directly
            // bypasses the builder's reject): a TCP/TLS TURN entry gathers no relay candidate — the TCP/TLS relay
            // data path is not wired into the media bundle.
            logger.LogWarning(
                "Skipping TURN server {Host} with transport {Transport}: only UDP TURN is supported for relay " +
                "gathering — no relay candidate is gathered for this server.",
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
        onCandidate(WebRtcIceCandidateFactory.RelayCandidate(allocation.RelayedEndPoint, relatedBase));
    }
}
