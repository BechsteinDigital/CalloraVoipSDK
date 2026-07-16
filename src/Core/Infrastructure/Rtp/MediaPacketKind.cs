namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// The kind of datagram received on a media 5-tuple, per the RFC 7983 / RFC 5764 §5.1.2 demux.
/// STUN, DTLS, RTP, and RTCP are multiplexed on the same socket; this is the first routing decision
/// a media transport makes for every inbound datagram.
/// </summary>
internal enum MediaPacketKind
{
    /// <summary>An RTP media packet — also the fallback when a datagram matches no other kind.</summary>
    Rtp = 0,

    /// <summary>An RTCP control packet (RFC 3550: version 2, packet type 192..223).</summary>
    Rtcp,

    /// <summary>A STUN connectivity-check packet (RFC 5389: first byte 0..3 with the magic cookie).</summary>
    Stun,

    /// <summary>A DTLS record (RFC 6347: content type 20..63) carrying the DTLS-SRTP handshake.</summary>
    Dtls,
}
