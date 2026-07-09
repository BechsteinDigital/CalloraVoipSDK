using System.Net;
using CalloraVoipSdk;

namespace CalloraVoipSdk.Core.Application.Ports.Connectivity;

/// <summary>
/// Port for ICE STUN interactions used by the application-layer ICE agent.
/// </summary>
internal interface IIceStunProbe
{
    /// <summary>
    /// Tries to resolve a server-reflexive endpoint for <paramref name="localEndPoint"/>
    /// via one configured STUN server. When <paramref name="sharedUdpSocket"/> is set,
    /// the query is sent through that already-bound socket (required while the media
    /// port is reserved by the call — a second bind would fail with EADDRINUSE).
    /// </summary>
    Task<IPEndPoint?> TryGetServerReflexiveEndPointAsync(
        IPEndPoint localEndPoint,
        IceServerConfiguration server,
        System.Net.Sockets.Socket? sharedUdpSocket = null,
        CancellationToken ct = default);

    /// <summary>
    /// Executes one ICE connectivity check toward <paramref name="remoteEndPoint"/>, carrying the
    /// RFC 8445 §7.2.2 check attributes: PRIORITY (<paramref name="localCandidatePriority"/>) and
    /// the role attribute (ICE-CONTROLLING when <paramref name="isControlling"/>, else
    /// ICE-CONTROLLED) with <paramref name="tieBreaker"/>.
    /// </summary>
    /// <param name="localCandidatePriority">Priority carried in the PRIORITY attribute.</param>
    /// <param name="isControlling">Whether this agent currently holds the controlling role.</param>
    /// <param name="tieBreaker">This agent's 64-bit ICE tie-breaker.</param>
    /// <param name="useCandidate">
    /// When <see langword="true"/> and the agent is controlling, adds USE-CANDIDATE to nominate
    /// the pair (RFC 8445 §8.1.1 regular nomination). Ignored for a controlled agent.
    /// </param>
    Task<bool> TryCheckConnectivityAsync(
        IPEndPoint localEndPoint,
        IPEndPoint remoteEndPoint,
        string localIceUfrag,
        string remoteIceUfrag,
        string remoteIcePassword,
        uint localCandidatePriority,
        bool isControlling,
        ulong tieBreaker,
        bool useCandidate,
        TimeSpan timeout,
        CancellationToken ct = default);
}
