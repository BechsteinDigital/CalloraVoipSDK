using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;

namespace CalloraVoipSdk.Core.Application.Media.Rtcp.Wire;

/// <summary>
/// Decodes and encodes RTCP compound packets (RFC 3550 §6).
/// A single UDP datagram may carry multiple concatenated RTCP packets —
/// this codec handles the full compound packet, not individual packets.
/// </summary>
internal interface IRtcpPacketCodec
{
    /// <summary>
    /// Parses a compound RTCP datagram into its constituent packets.
    /// Throws <see cref="ArgumentException"/> if the data is malformed.
    /// </summary>
    IReadOnlyList<RtcpPacket> Decode(ReadOnlySpan<byte> data);

    /// <summary>
    /// Serialises one or more RTCP packets into a compound RTCP datagram.
    /// The list must not be empty.
    /// </summary>
    byte[] Encode(IReadOnlyList<RtcpPacket> packets);
}
