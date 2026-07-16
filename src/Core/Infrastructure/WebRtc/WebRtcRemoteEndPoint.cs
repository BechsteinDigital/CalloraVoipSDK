using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;

namespace CalloraVoipSdk.Core.Infrastructure.WebRtc;

/// <summary>
/// Resolves the transport address a WebRTC peer sends media to from the peer's description (Weg 1,
/// RFC 8839). A WebRTC offer/answer carries the real address(es) in <c>a=candidate</c> and leaves the
/// m-line port a placeholder (typically 9), so the highest-priority component-1 UDP host/server-reflexive
/// candidate is preferred; when none is present the m-line connection address and port are used (the
/// loopback / SIP style, where the port is real).
/// </summary>
/// <remarks>
/// This is single-candidate selection, not full ICE: it picks the best advertised address rather than
/// running connectivity checks across every candidate pair. That suffices for host-reachable peers
/// (loopback, same LAN); NAT traversal via srflx/relay pairing is later work.
/// </remarks>
internal static class WebRtcRemoteEndPoint
{
    /// <summary>
    /// Resolves the remote media endpoint, or <see langword="null"/> when neither a usable candidate nor
    /// a real m-line address/port is present.
    /// </summary>
    public static IPEndPoint? Resolve(SdpMediaDescription remoteAudio, string? sessionConnectionAddress)
    {
        ArgumentNullException.ThrowIfNull(remoteAudio);

        var candidate = remoteAudio.Candidates
            .Where(c => c.Component == 1 // RTP; rtcp-mux shares it (RFC 8843)
                        && c.Transport.Equals("udp", StringComparison.OrdinalIgnoreCase)
                        && c.Port > 0
                        && IPAddress.TryParse(c.Address, out _))
            .OrderByDescending(c => c.Priority)
            .FirstOrDefault();
        if (candidate is not null)
            return new IPEndPoint(IPAddress.Parse(candidate.Address), candidate.Port);

        var address = remoteAudio.ConnectionAddress ?? sessionConnectionAddress;
        return address is not null && remoteAudio.Port > 0 && IPAddress.TryParse(address, out var ip)
            ? new IPEndPoint(ip, remoteAudio.Port)
            : null;
    }
}
