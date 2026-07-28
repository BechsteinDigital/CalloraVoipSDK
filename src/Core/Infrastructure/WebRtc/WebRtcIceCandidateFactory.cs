using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;

namespace CalloraVoipSdk.Core.Infrastructure.WebRtc;

/// <summary>
/// Builds the RFC 8839 ICE candidates a <see cref="WebRtcPeerConnection"/> advertises — host, server-reflexive
/// and relay — from the bound/discovered/allocated transport endpoints. Pure and stateless (RFC 8445 §5.1.2.1
/// priorities): extracted from the peer so the connection type stays focused on lifecycle and signalling.
/// </summary>
internal static class WebRtcIceCandidateFactory
{
    // A host ICE candidate for the bound local media endpoint (RFC 8445 §5.1.2.1 priority: host type-pref
    // 126, local-pref 65535, RTP component 1). rtcp-mux shares component 1, so no RTCP candidate is needed.
    public static SdpIceCandidate LocalHostCandidate(IPEndPoint local) => new()
    {
        Foundation = "1",
        Component = 1,
        Transport = "udp",
        Priority = (126L << 24) | (65535L << 8) | 255L,
        Address = local.Address.ToString(),
        Port = local.Port,
        Type = "host",
    };

    // A server-reflexive candidate for the STUN-discovered public endpoint (RFC 8445 §5.1.2.1 priority:
    // srflx type-pref 100, local-pref 65535, RTP component 1). raddr/rport carry the local base (host).
    public static SdpIceCandidate ServerReflexiveCandidate(IPEndPoint reflexive, IPEndPoint host) => new()
    {
        Foundation = "2",
        Component = 1,
        Transport = "udp",
        Priority = (100L << 24) | (65535L << 8) | 255L,
        Address = reflexive.Address.ToString(),
        Port = reflexive.Port,
        Type = "srflx",
        RelatedAddress = host.Address.ToString(),
        RelatedPort = host.Port,
    };

    // A relay candidate for the TURN-allocated relayed endpoint (RFC 8445 §5.1.2.1 priority: relay type-pref
    // 0, local-pref 65535, RTP component 1). raddr/rport carry the base the relay relates to (RFC 8839): the
    // server-reflexive address from the Allocate response when present, else the local host base.
    public static SdpIceCandidate RelayCandidate(IPEndPoint relayed, IPEndPoint relatedBase) => new()
    {
        Foundation = "3",
        Component = 1,
        Transport = "udp",
        Priority = (0L << 24) | (65535L << 8) | 255L,
        Address = relayed.Address.ToString(),
        Port = relayed.Port,
        Type = "relay",
        RelatedAddress = relatedBase.Address.ToString(),
        RelatedPort = relatedBase.Port,
    };
}
