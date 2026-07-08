using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;

/// <summary>
/// Resolves the local IP address to advertise for media (SDP connection line and RTP bind).
/// The signaling bind address is only authoritative when it is a concrete non-loopback
/// address, or when the remote peer itself is loopback (local test setups). Otherwise the
/// OS routing table is probed towards the remote signaling endpoint — no DNS involved —
/// with the SIP URI host as fallback. Loopback is never returned silently for a
/// non-loopback peer: that combination produced unusable SDP (the peer terminates the
/// call right after answering) and RTP/RTCP sockets that cannot reach the remote.
/// </summary>
internal static class AdvertisedMediaAddressResolver
{
    /// <summary>
    /// Resolves the advertised media address for one session.
    /// <paramref name="probeRoute"/> returns the local address the OS would use to reach
    /// the given endpoint, or <see langword="null"/> when no route is available.
    /// </summary>
    public static IPAddress Resolve(
        ISipCallSession session,
        Func<string, int, IPAddress?> probeRoute,
        ILogger logger)
    {
        var localAddress = session.LocalSignalingEndPoint.Address;
        var remote = session.RemoteSignalingEndPoint;

        var isWildcard = IPAddress.Any.Equals(localAddress) || IPAddress.IPv6Any.Equals(localAddress);
        var isLoopbackTowardsRemotePeer = IPAddress.IsLoopback(localAddress)
            && remote is not null
            && !IPAddress.IsLoopback(remote.Address);

        if (!isWildcard && !isLoopbackTowardsRemotePeer)
            return localAddress;

        if (remote is not null)
        {
            var viaRemote = probeRoute(remote.Address.ToString(), remote.Port);
            if (viaRemote is not null && !IPAddress.Any.Equals(viaRemote) && !IPAddress.IPv6Any.Equals(viaRemote))
                return viaRemote;
        }

        var remoteUri = SipProtocol.ExtractUriFromNameAddr(session.RemoteUri) ?? session.RemoteUri;
        if (SipProtocol.TryParseSipUri(remoteUri, out _, out var remoteHost, out var remotePort)
            && !string.IsNullOrWhiteSpace(remoteHost))
        {
            var viaUri = probeRoute(remoteHost, remotePort ?? 5060);
            if (viaUri is not null && !IPAddress.Any.Equals(viaUri) && !IPAddress.IPv6Any.Equals(viaUri))
                return viaUri;
        }

        logger.LogWarning(
            "Could not resolve a routable local media address for SIP session {CallId} " +
            "(local signaling {Local}, remote {Remote}); advertising loopback, which the " +
            "remote peer will most likely reject.",
            session.CallId,
            session.LocalSignalingEndPoint,
            remote?.ToString() ?? session.RemoteUri);
        return IPAddress.Loopback;
    }

    /// <summary>
    /// Default route probe: a connected (but never sending) UDP socket lets the OS pick
    /// the outgoing interface for the destination. Returns <see langword="null"/> when
    /// the destination is unroutable or resolution fails.
    /// </summary>
    public static IPAddress? ProbeRoute(string host, int port)
    {
        try
        {
            using var probe = new UdpClient();
            probe.Connect(host, port);
            return probe.Client.LocalEndPoint is IPEndPoint discovered
                ? discovered.Address
                : null;
        }
        catch (SocketException)
        {
            return null;
        }
    }
}
