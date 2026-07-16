using System.Buffers.Binary;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Classifies a datagram arriving on a media 5-tuple into its <see cref="MediaPacketKind"/>
/// (RFC 7983 / RFC 5764 §5.1.2). STUN, DTLS, RTP, and RTCP share the socket; their first-byte ranges
/// are disjoint (STUN 0..3, DTLS 20..63, RTP/RTCP 128..191), so a single look routes the packet.
/// Extracted from the single-stream <see cref="Session.RtpSession"/> so the bundled transport
/// (RFC 8843) reuses the exact same demux instead of forking it.
/// </summary>
internal static class MediaPacketClassifier
{
    /// <summary>
    /// Returns the kind of the datagram from its bytes alone. Routing <em>policy</em> stays with the
    /// caller (for example, only treating STUN/DTLS specially when the datagram carries a source
    /// endpoint) — this method is pure and has no side effects.
    /// </summary>
    public static MediaPacketKind Classify(ReadOnlySpan<byte> datagram)
    {
        if (IsStun(datagram))
            return MediaPacketKind.Stun;
        if (IsDtls(datagram))
            return MediaPacketKind.Dtls;
        if (IsRtcp(datagram))
            return MediaPacketKind.Rtcp;
        return MediaPacketKind.Rtp;
    }

    // RFC 7983 / RFC 5764 §5.1.2: a STUN packet's first byte is in 0..3, disjoint from RTP/RTCP
    // (128..191). The 32-bit magic cookie (RFC 5389 §6) at offset 4 confirms it and rejects any
    // stray low-byte datagram that is not STUN.
    private static bool IsStun(ReadOnlySpan<byte> datagram) =>
        datagram.Length >= 8
        && datagram[0] <= 3
        && BinaryPrimitives.ReadUInt32BigEndian(datagram[4..8]) == 0x2112A442u;

    // RFC 5764 §5.1.2 / RFC 7983: a DTLS record's first byte (content type) is in 20..63.
    // The 13-byte minimum is the DTLS record header — anything shorter cannot be DTLS.
    private static bool IsDtls(ReadOnlySpan<byte> datagram) =>
        datagram.Length >= 13 && datagram[0] is >= 20 and <= 63;

    // RFC 3550 / RFC 7983: an RTCP packet has version 2 and a packet type in 192..223, which
    // distinguishes it from RTP (whose payload type, byte 1 low 7 bits, stays below 192).
    private static bool IsRtcp(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length < 2)
            return false;

        var version = datagram[0] >> 6;
        return version == 2 && datagram[1] is >= 192 and <= 223;
    }
}
