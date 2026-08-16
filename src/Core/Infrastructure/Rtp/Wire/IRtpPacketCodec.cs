using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;

/// <summary>
/// Encodes and decodes RTP packets to and from their binary wire format (RFC 3550 §5).
/// </summary>
internal interface IRtpPacketCodec
{
    /// <summary>
    /// Decodes a raw UDP datagram into an <see cref="RtpPacket"/>.
    /// Throws <see cref="FormatException"/> when the datagram is shorter than the
    /// minimum 12-byte RTP header or carries an unsupported version.
    /// </summary>
    RtpPacket Decode(ReadOnlySpan<byte> datagram);

    /// <summary>
    /// Encodes an <see cref="RtpPacket"/> to its binary wire representation. The packet is our own model, not
    /// wire input, so a field that does not fit the RTP header is rejected rather than normalised onto the wire
    /// (#161 P3-14): an unsupported version, a payload type above 127, more than 15 CSRCs, or a header
    /// extension longer than its 16-bit word count can express.
    /// </summary>
    /// <exception cref="ArgumentException">The packet cannot be represented in an RTP header.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The payload type is outside the seven-bit field.</exception>
    byte[] Encode(RtpPacket packet);
}
