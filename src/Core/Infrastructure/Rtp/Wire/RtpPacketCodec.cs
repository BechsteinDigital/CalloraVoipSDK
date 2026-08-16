using System.Buffers.Binary;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;

/// <summary>
/// Binary codec for RTP packets (RFC 3550 §5).
/// Handles the fixed header, CSRC list, optional header extension, payload, and padding.
/// </summary>
internal sealed class RtpPacketCodec : IRtpPacketCodec
{
    private const int MinHeaderSize = 12;
    private const byte SupportedVersion = 2;

    /// <summary>Widest payload type the seven-bit PT field holds (RFC 3550 §5.1).</summary>
    private const byte MaxPayloadType = 127;

    /// <summary>Most contributing sources the four-bit CC field can count (RFC 3550 §5.1).</summary>
    private const int MaxCsrcCount = 15;

    /// <summary>Most 32-bit words the header extension's 16-bit length field can express (RFC 3550 §5.3.1).</summary>
    private const int MaxExtensionWords = ushort.MaxValue;

    // -------------------------------------------------------------------------
    // Decode
    // -------------------------------------------------------------------------

    /// <inheritdoc />
    public RtpPacket Decode(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length < MinHeaderSize)
            throw new FormatException($"RTP datagram too short: {datagram.Length} bytes (minimum {MinHeaderSize}).");

        // Byte 0: V(2)|P(1)|X(1)|CC(4)
        var byte0 = datagram[0];
        var version = (byte)(byte0 >> 6);
        if (version != SupportedVersion)
            throw new FormatException($"Unsupported RTP version {version}; only version 2 is supported.");

        var hasPadding  = (byte0 & 0x20) != 0;
        var hasExtension = (byte0 & 0x10) != 0;
        var csrcCount   = byte0 & 0x0F;

        // Byte 1: M(1)|PT(7)
        var byte1      = datagram[1];
        var marker     = (byte1 & 0x80) != 0;
        var payloadType = (byte)(byte1 & 0x7F);

        var sequenceNumber = BinaryPrimitives.ReadUInt16BigEndian(datagram[2..]);
        var timestamp      = BinaryPrimitives.ReadUInt32BigEndian(datagram[4..]);
        var ssrc           = BinaryPrimitives.ReadUInt32BigEndian(datagram[8..]);

        var offset = MinHeaderSize;

        // CSRC list
        var csrc = new uint[csrcCount];
        for (var i = 0; i < csrcCount; i++)
        {
            if (offset + 4 > datagram.Length)
                throw new FormatException("RTP datagram truncated inside CSRC list.");
            csrc[i] = BinaryPrimitives.ReadUInt32BigEndian(datagram[offset..]);
            offset += 4;
        }

        // Header extension (RFC 3550 §5.3.1)
        RtpExtension? extension = null;
        if (hasExtension)
        {
            if (offset + 4 > datagram.Length)
                throw new FormatException("RTP datagram truncated at header extension.");
            var profile = BinaryPrimitives.ReadUInt16BigEndian(datagram[offset..]);
            // Length field counts 32-bit words following the 4-byte prefix
            var extWords  = BinaryPrimitives.ReadUInt16BigEndian(datagram[(offset + 2)..]);
            var extBytes  = extWords * 4;
            offset += 4;
            if (offset + extBytes > datagram.Length)
                throw new FormatException("RTP datagram truncated inside header extension data.");
            extension = new RtpExtension
            {
                Profile = profile,
                Data    = datagram.Slice(offset, extBytes).ToArray()
            };
            offset += extBytes;
        }

        // Payload (with padding stripped)
        var payloadSpan = datagram[offset..];
        if (hasPadding)
        {
            if (payloadSpan.IsEmpty)
                throw new FormatException("RTP padding flag set but no payload bytes remain.");
            var paddingCount = payloadSpan[^1];
            if (paddingCount == 0 || paddingCount > payloadSpan.Length)
                throw new FormatException($"Invalid RTP padding count {paddingCount}.");
            payloadSpan = payloadSpan[..^paddingCount];
        }

        return new RtpPacket
        {
            Version        = SupportedVersion,
            Padding        = hasPadding,
            Marker         = marker,
            PayloadType    = payloadType,
            SequenceNumber = sequenceNumber,
            Timestamp      = timestamp,
            Ssrc           = ssrc,
            Csrc           = csrc,
            HeaderExtension = extension,
            Payload        = payloadSpan.ToArray()
        };
    }

    // -------------------------------------------------------------------------
    // Encode
    // -------------------------------------------------------------------------

    /// <inheritdoc />
    public byte[] Encode(RtpPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        // Encode's input is our own model, never wire input, so a value that does not fit the RTP header is a
        // bug on this side — and the K4 contract that keeps Decode lenient works the other way round here.
        // Every one of these used to be normalised away silently (#161 P3-14): a payload type above 127 lost
        // its top bit, landing on an entirely different codec (or, at 200/201, inside the RTCP range on a muxed
        // socket); CSRCs past the fifteenth were dropped while the surviving ones still claimed to be the whole
        // contributing-source list; and an extension longer than the 16-bit word count can express wrapped it,
        // making the receiver read the header length as a small number and the rest of the extension as payload.
        if (packet.Version != SupportedVersion)
            throw new ArgumentException(
                $"RTP version {packet.Version} cannot be encoded; only version {SupportedVersion} is supported.",
                nameof(packet));

        if (packet.PayloadType > MaxPayloadType)
            throw new ArgumentOutOfRangeException(
                nameof(packet), packet.PayloadType,
                $"RTP payload type must be 0..{MaxPayloadType} — the header field is seven bits.");

        if (packet.Csrc.Count > MaxCsrcCount)
            throw new ArgumentException(
                $"An RTP packet carries at most {MaxCsrcCount} CSRCs (the CC field is four bits); this one has " +
                $"{packet.Csrc.Count}.", nameof(packet));

        var csrcCount     = packet.Csrc.Count;
        var extData       = packet.HeaderExtension?.Data ?? ReadOnlyMemory<byte>.Empty;
        if (RoundUp4(extData.Length) / 4 > MaxExtensionWords)
            throw new ArgumentException(
                $"An RTP header extension carries at most {MaxExtensionWords} 32-bit words (RFC 3550 §5.3.1); " +
                $"this one is {extData.Length} bytes.", nameof(packet));

        var extBytes      = packet.HeaderExtension is not null ? 4 + RoundUp4(extData.Length) : 0;
        var payloadLength = packet.Payload.Length;

        var totalLength = MinHeaderSize + csrcCount * 4 + extBytes + payloadLength;
        var buffer      = new byte[totalLength];
        var span        = buffer.AsSpan();

        // Byte 0
        var hasExtension = packet.HeaderExtension is not null;
        span[0] = (byte)(
            (SupportedVersion << 6) |
            (packet.Padding   ? 0x20 : 0) |
            (hasExtension     ? 0x10 : 0) |
            (csrcCount & 0x0F));

        // Byte 1
        span[1] = (byte)((packet.Marker ? 0x80 : 0) | (packet.PayloadType & 0x7F));

        BinaryPrimitives.WriteUInt16BigEndian(span[2..], packet.SequenceNumber);
        BinaryPrimitives.WriteUInt32BigEndian(span[4..], packet.Timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..], packet.Ssrc);

        var offset = MinHeaderSize;

        for (var i = 0; i < csrcCount; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(span[offset..], packet.Csrc[i]);
            offset += 4;
        }

        if (hasExtension)
        {
            BinaryPrimitives.WriteUInt16BigEndian(span[offset..], packet.HeaderExtension!.Profile);
            var paddedWords = RoundUp4(extData.Length) / 4;
            BinaryPrimitives.WriteUInt16BigEndian(span[(offset + 2)..], (ushort)paddedWords);
            offset += 4;
            extData.Span[..extData.Length].CopyTo(span[offset..]);
            offset += RoundUp4(extData.Length);
        }

        packet.Payload.Span.CopyTo(span[offset..]);

        return buffer;
    }

    private static int RoundUp4(int value) => (value + 3) & ~3;
}
