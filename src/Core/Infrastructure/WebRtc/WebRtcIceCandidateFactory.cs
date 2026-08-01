using System.Net;
using System.Globalization;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;

namespace CalloraVoipSdk.Core.Infrastructure.WebRtc;

/// <summary>
/// Builds the RFC 8839 ICE candidates a <see cref="WebRtcPeerConnection"/> advertises — host, server-reflexive
/// and relay — from the bound/discovered/allocated transport endpoints. Pure and stateless (RFC 8445 §5.1.2.1
/// priorities): extracted from the peer so the connection type stays focused on lifecycle and signalling.
/// </summary>
internal static class WebRtcIceCandidateFactory
{
    /// <summary>
    /// Returns whether a bound endpoint has an address that can be advertised as an ICE host candidate.
    /// Wildcard bind addresses describe socket ownership, not a peer-reachable interface (RFC 8445 §5.1.1.1),
    /// so <c>0.0.0.0</c> and <c>::</c> must not enter SDP or trickle signalling.
    /// </summary>
    public static bool CanAdvertiseLocalHost(IPEndPoint local) =>
        !local.Address.Equals(IPAddress.Any) && !local.Address.Equals(IPAddress.IPv6Any);

    // A host ICE candidate for the bound local media endpoint (RFC 8445 §5.1.2.1 priority: host type-pref
    // 126, local-pref 65535, RTP component 1). rtcp-mux shares component 1, so no RTCP candidate is needed.
    // Foundations are type-scoped (RFC 8445 §5.1.1.3 — same foundation only for same type+base+server+transport):
    // host bases are "h1", "h2", …, distinct from the fixed srflx ("s1") and relay ("r1") foundations, so a
    // multi-homed second host no longer collides with srflx and freeze the peer's NAT/relay fallback wrongly.
    public static SdpIceCandidate LocalHostCandidate(IPEndPoint local, int preferenceIndex = 0) => new()
    {
        Foundation = "h" + (preferenceIndex + 1).ToString(CultureInfo.InvariantCulture),
        Component = 1,
        Transport = "udp",
        Priority = (126L << 24) | ((65535L - Math.Min(preferenceIndex, 65535)) << 8) | 255L,
        Address = local.Address.ToString(),
        Port = local.Port,
        Type = "host",
    };

    // A server-reflexive candidate for the STUN-discovered public endpoint (RFC 8445 §5.1.2.1 priority:
    // srflx type-pref 100, local-pref 65535, RTP component 1). raddr/rport carry the local base (host).
    public static SdpIceCandidate ServerReflexiveCandidate(IPEndPoint reflexive, IPEndPoint? host) => new()
    {
        Foundation = "s1",
        Component = 1,
        Transport = "udp",
        Priority = (100L << 24) | (65535L << 8) | 255L,
        Address = reflexive.Address.ToString(),
        Port = reflexive.Port,
        Type = "srflx",
        RelatedAddress = host is not null && CanAdvertiseLocalHost(host) ? host.Address.ToString() : null,
        RelatedPort = host is not null && CanAdvertiseLocalHost(host) ? host.Port : null,
    };

    // A relay candidate for the TURN-allocated relayed endpoint (RFC 8445 §5.1.2.1 priority: relay type-pref
    // 0, local-pref 65535, RTP component 1). raddr/rport carry the base the relay relates to (RFC 8839): the
    // server-reflexive address from the Allocate response when present, else the local host base.
    public static SdpIceCandidate RelayCandidate(IPEndPoint relayed, IPEndPoint? relatedBase) => new()
    {
        Foundation = "r1",
        Component = 1,
        Transport = "udp",
        Priority = (0L << 24) | (65535L << 8) | 255L,
        Address = relayed.Address.ToString(),
        Port = relayed.Port,
        Type = "relay",
        RelatedAddress = relatedBase is not null && CanAdvertiseLocalHost(relatedBase) ? relatedBase.Address.ToString() : null,
        RelatedPort = relatedBase is not null && CanAdvertiseLocalHost(relatedBase) ? relatedBase.Port : null,
    };

    /// <summary>
    /// Validates a trickled RFC 8829 candidate string (<c>candidate:…</c>, tolerating a leading <c>a=</c>) and
    /// returns the parsed fields — the inverse of the builders above. The address is NOT parsed to an IP, so an
    /// mDNS <c>.local</c> name stays distinguishable by the caller. Returns null when malformed/unusable (wrong
    /// component/transport, non-positive port, negative priority).
    /// </summary>
    public static SdpIceCandidate? ParseTrickleCandidate(string candidate)
    {
        var value = candidate.Trim();
        if (value.StartsWith("a=", StringComparison.Ordinal))
            value = value[2..];
        if (value.StartsWith("candidate:", StringComparison.Ordinal))
            value = value["candidate:".Length..];

        if (SdpIceCandidate.TryParse(value) is not { } parsed
            || parsed.Component != 1
            || !parsed.Transport.Equals("udp", StringComparison.OrdinalIgnoreCase)
            || parsed.Port <= 0
            || parsed.Priority < 0) // RFC 8445 priority is a 31-bit unsigned; a negative value is malformed
            return null;

        return parsed;
    }
}
