using System.Buffers.Binary;

namespace CalloraVoipSdk.Core.Infrastructure.Turn.Wire;

/// <summary>
/// Codec for TURN ChannelData framing (RFC 8656 §11.6).
/// </summary>
internal static class TurnChannelDataCodec
{
    /// <summary>
    /// Tries to parse a datagram as ChannelData packet.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> packet, out ushort channelNumber, out byte[] data)
    {
        channelNumber = 0;
        data = Array.Empty<byte>();

        if (packet.Length < 4)
            return false;

        var channel = BinaryPrimitives.ReadUInt16BigEndian(packet);
        if (channel < 0x4000 || channel > 0x7FFF)
            return false;

        ushort length = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        if (packet.Length < 4 + length)
            return false;

        channelNumber = channel;
        data = packet.Slice(4, length).ToArray();
        return true;
    }

    /// <summary>
    /// Encodes a ChannelData packet (RFC 8656 §11.6). Over a stream transport (TCP/TLS) pass
    /// <paramref name="padToFourBytes"/> so the frame is padded to a 4-byte boundary with 0-3 bytes that are
    /// <em>not</em> counted in the length field (RFC 8656 §12.5) — this keeps the next framed message aligned.
    /// Over UDP no padding is added (each datagram is one frame).
    /// </summary>
    public static byte[] Encode(ushort channelNumber, ReadOnlySpan<byte> data, bool padToFourBytes = false)
    {
        if (channelNumber < 0x4000 || channelNumber > 0x7FFF)
            throw new ArgumentOutOfRangeException(nameof(channelNumber), "TURN channel number must be in range 0x4000..0x7FFF.");

        if (data.Length > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(data), "TURN ChannelData payload exceeds 65535 bytes.");

        var padding = padToFourBytes ? (4 - (data.Length & 3)) & 3 : 0;
        var packet = new byte[4 + data.Length + padding];
        BinaryPrimitives.WriteUInt16BigEndian(packet, channelNumber);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)data.Length);   // length excludes padding
        data.CopyTo(packet.AsSpan(4));
        // The trailing padding bytes stay zero.
        return packet;
    }
}
