using System.Buffers.Binary;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp.Retransmission;

/// <summary>
/// RTX retransmission packet format (RFC 4588 §4). A retransmitted RTP packet is resent
/// on a separate stream with its own payload type, SSRC, and monotonically increasing
/// sequence number, so it never collides with the original stream nor trips the SRTP
/// replay window (the reason plain resends over SRTP fail). The original sequence number
/// is preserved as a 2-byte OSN prefix on the payload; timestamp and marker are copied
/// from the original packet.
/// </summary>
internal static class RtxPacketFactory
{
    private const int OsnLength = 2;

    /// <summary>
    /// Wraps an original packet as an RTX packet for retransmission (RFC 4588 §4): rtx
    /// payload type and SSRC, the caller's fresh rtx sequence number, and the original
    /// sequence number prepended to the payload.
    /// DECISION: the original's header extensions and CSRC list are intentionally not
    /// carried across — RFC 4588 §4 defines the RTX payload as OSN + original payload only,
    /// and a repair packet's own extensions (abs-send-time, transport-cc) differ from the
    /// original's. The recovered packet from <see cref="TryDecapsulate"/> therefore has none.
    /// </summary>
    public static RtpPacket Encapsulate(
        RtpPacket original, byte rtxPayloadType, uint rtxSsrc, ushort rtxSequenceNumber)
    {
        ArgumentNullException.ThrowIfNull(original);

        var payload = new byte[OsnLength + original.Payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(payload, original.SequenceNumber);
        original.Payload.Span.CopyTo(payload.AsSpan(OsnLength));

        return new RtpPacket
        {
            PayloadType = rtxPayloadType,
            Marker = original.Marker,
            SequenceNumber = rtxSequenceNumber,
            Timestamp = original.Timestamp,
            Ssrc = rtxSsrc,
            Payload = payload,
        };
    }

    /// <summary>
    /// Recovers the original packet from an RTX packet (RFC 4588 §4): strips the OSN prefix,
    /// restoring the original sequence number, payload type, and SSRC. The recovered packet
    /// carries no header extensions or CSRC list (see <see cref="Encapsulate"/> — they are
    /// not part of the RTX payload). Returns <see langword="false"/> when the payload is too
    /// short to hold an OSN.
    /// </summary>
    public static bool TryDecapsulate(
        RtpPacket rtx, byte originalPayloadType, uint originalSsrc, out RtpPacket? original)
    {
        ArgumentNullException.ThrowIfNull(rtx);
        original = null;

        if (rtx.Payload.Length < OsnLength)
            return false;

        var originalSequence = BinaryPrimitives.ReadUInt16BigEndian(rtx.Payload.Span);
        original = new RtpPacket
        {
            PayloadType = originalPayloadType,
            Marker = rtx.Marker,
            SequenceNumber = originalSequence,
            Timestamp = rtx.Timestamp,
            Ssrc = originalSsrc,
            Payload = rtx.Payload[OsnLength..],
        };
        return true;
    }
}
