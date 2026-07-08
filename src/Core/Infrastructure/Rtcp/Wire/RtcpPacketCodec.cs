using System.Buffers.Binary;
using System.Text;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;

/// <summary>
/// Encodes and decodes RTCP compound packets (RFC 3550 §6).
///
/// Wire layout:
///   Each RTCP packet:  [header(4)] [body(variable, padded to 4 bytes)]
///   Header byte 0:     V(2) | P(1) | RC/SC(5)
///   Header byte 1:     PT (packet type)
///   Header bytes 2-3:  length — number of 32-bit words minus one (RFC 3550 §6.1)
///
/// Padding: if the P bit is set in a packet's header, the last byte of that
/// packet's data holds the number of padding bytes to ignore (including itself).
/// </summary>
internal sealed class RtcpPacketCodec : IRtcpPacketCodec
{
    // -------------------------------------------------------------------------
    // Decode
    // -------------------------------------------------------------------------

    public IReadOnlyList<RtcpPacket> Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            throw new ArgumentException("RTCP compound packet is empty.", nameof(data));

        var packets = new List<RtcpPacket>();
        var offset  = 0;

        while (offset < data.Length)
        {
            if (data.Length - offset < 4)
                throw new ArgumentException("Truncated RTCP header.");

            var b0         = data[offset];
            var version    = (b0 >> 6) & 0x03;
            if (version != 2)
                throw new ArgumentException($"RTCP version must be 2, got {version}.");

            var hasPadding = (b0 & 0x20) != 0;
            var count      = b0 & 0x1F;
            var pt         = (RtcpPacketType)data[offset + 1];
            var length     = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
            var packetLen  = (length + 1) * 4;

            if (data.Length - offset < packetLen)
                throw new ArgumentException(
                    $"RTCP packet claims {packetLen} bytes but only {data.Length - offset} remain.");

            var raw        = data.Slice(offset, packetLen);
            var bodyEnd    = packetLen;

            if (hasPadding)
            {
                var padCount = raw[packetLen - 1];
                if (padCount == 0 || padCount > packetLen - 4)
                    throw new ArgumentException($"Invalid RTCP padding count {padCount}.");
                bodyEnd -= padCount;
            }

            // Body starts after the 4-byte common header
            var body = raw[4..bodyEnd];

            // RFC 3550 §6.1: unrecognized packet types (e.g. RTCP-XR, type 207, RFC 3611)
            // are skipped via the length field — throwing here would discard the whole
            // compound datagram including the SR/RR it starts with.
            RtcpPacket? packet = pt switch
            {
                RtcpPacketType.SenderReport   => DecodeSr(body, count),
                RtcpPacketType.ReceiverReport => DecodeRr(body, count),
                RtcpPacketType.Sdes           => DecodeSdes(body, count),
                RtcpPacketType.Bye            => DecodeBye(body, count),
                _ => null,
            };

            if (packet is not null)
                packets.Add(packet);
            offset += packetLen;
        }

        return packets;
    }

    // -------------------------------------------------------------------------
    // Decode — individual packet types
    // -------------------------------------------------------------------------

    private static RtcpSenderReport DecodeSr(ReadOnlySpan<byte> body, int rc)
    {
        // body = SSRC(4) + sender-info(20) + RC*report-block(24 each)
        if (body.Length < 24)
            throw new ArgumentException("SR body too short.");

        var ssrc        = BinaryPrimitives.ReadUInt32BigEndian(body);
        var ntpSec      = BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
        var ntpFrac     = BinaryPrimitives.ReadUInt32BigEndian(body[8..]);
        var rtpTs       = BinaryPrimitives.ReadUInt32BigEndian(body[12..]);
        var pktCount    = BinaryPrimitives.ReadUInt32BigEndian(body[16..]);
        var octetCount  = BinaryPrimitives.ReadUInt32BigEndian(body[20..]);

        var blocks = DecodeReportBlocks(body[24..], rc);

        return new RtcpSenderReport
        {
            Ssrc              = ssrc,
            NtpTimestamp      = ((ulong)ntpSec << 32) | ntpFrac,
            RtpTimestamp      = rtpTs,
            SenderPacketCount = pktCount,
            SenderOctetCount  = octetCount,
            ReportBlocks      = blocks,
        };
    }

    private static RtcpReceiverReport DecodeRr(ReadOnlySpan<byte> body, int rc)
    {
        // body = SSRC(4) + RC*report-block(24 each)
        if (body.Length < 4)
            throw new ArgumentException("RR body too short.");

        var ssrc   = BinaryPrimitives.ReadUInt32BigEndian(body);
        var blocks = DecodeReportBlocks(body[4..], rc);

        return new RtcpReceiverReport { Ssrc = ssrc, ReportBlocks = blocks };
    }

    private static IReadOnlyList<RtcpReportBlock> DecodeReportBlocks(ReadOnlySpan<byte> data, int count)
    {
        if (data.Length < count * 24)
            throw new ArgumentException($"Not enough bytes for {count} report blocks.");

        var blocks = new RtcpReportBlock[count];
        for (var i = 0; i < count; i++)
        {
            var b    = data[(i * 24)..];
            var ssrc = BinaryPrimitives.ReadUInt32BigEndian(b);

            // b[4] = fraction lost; b[5..7] = cumulative packets lost (24-bit signed)
            var lostRaw = (int)((uint)(b[5] << 16 | b[6] << 8 | b[7]));
            if ((lostRaw & 0x800000) != 0)
                lostRaw |= unchecked((int)0xFF000000); // sign-extend to 32 bits

            blocks[i] = new RtcpReportBlock
            {
                Ssrc                = ssrc,
                FractionLost        = b[4],
                CumulativePacketsLost = lostRaw,
                ExtendedHighestSeq  = BinaryPrimitives.ReadUInt32BigEndian(b[8..]),
                Jitter              = BinaryPrimitives.ReadUInt32BigEndian(b[12..]),
                LastSr              = BinaryPrimitives.ReadUInt32BigEndian(b[16..]),
                DelaySinceLastSr    = BinaryPrimitives.ReadUInt32BigEndian(b[20..]),
            };
        }

        return blocks;
    }

    private static RtcpSdesPacket DecodeSdes(ReadOnlySpan<byte> body, int sc)
    {
        var chunks = new List<RtcpSdesChunk>(sc);
        var offset = 0;

        for (var i = 0; i < sc; i++)
        {
            if (body.Length - offset < 4)
                throw new ArgumentException("SDES chunk too short for SSRC.");

            var ssrc = BinaryPrimitives.ReadUInt32BigEndian(body[offset..]);
            offset += 4;

            var items = new List<RtcpSdesItem>();

            while (offset < body.Length)
            {
                var itemType = (RtcpSdesItemType)body[offset];
                offset++;

                if (itemType == RtcpSdesItemType.End)
                {
                    // Skip padding to next 4-byte boundary
                    var chunkStart = (offset - 5) & ~3; // re-align from SSRC start
                    // Simpler: just align offset to next multiple of 4 relative to body start
                    while (offset % 4 != 0) offset++;
                    break;
                }

                if (offset >= body.Length)
                    throw new ArgumentException("Truncated SDES item.");

                var valueLen = body[offset++];
                if (offset + valueLen > body.Length)
                    throw new ArgumentException("SDES item value exceeds available data.");

                var value = Encoding.UTF8.GetString(body.Slice(offset, valueLen));
                offset += valueLen;

                items.Add(new RtcpSdesItem { ItemType = itemType, Value = value });
            }

            chunks.Add(new RtcpSdesChunk { Ssrc = ssrc, Items = items });
        }

        return new RtcpSdesPacket { Chunks = chunks };
    }

    private static RtcpByePacket DecodeBye(ReadOnlySpan<byte> body, int sc)
    {
        if (body.Length < sc * 4)
            throw new ArgumentException($"BYE body too short for {sc} sources.");

        var sources = new uint[sc];
        for (var i = 0; i < sc; i++)
            sources[i] = BinaryPrimitives.ReadUInt32BigEndian(body[(i * 4)..]);

        string? reason = null;
        var afterSources = sc * 4;
        if (body.Length > afterSources)
        {
            var reasonLen = body[afterSources];
            if (afterSources + 1 + reasonLen <= body.Length)
                reason = Encoding.UTF8.GetString(body.Slice(afterSources + 1, reasonLen));
        }

        return new RtcpByePacket { Sources = sources, Reason = reason };
    }

    // -------------------------------------------------------------------------
    // Encode
    // -------------------------------------------------------------------------

    public byte[] Encode(IReadOnlyList<RtcpPacket> packets)
    {
        if (packets.Count == 0)
            throw new ArgumentException("Cannot encode an empty RTCP compound packet.", nameof(packets));

        var parts = packets.Select(EncodeSingle).ToArray();
        var total = parts.Sum(p => p.Length);
        var buf   = new byte[total];
        var pos   = 0;
        foreach (var part in parts)
        {
            part.CopyTo(buf, pos);
            pos += part.Length;
        }
        return buf;
    }

    private static byte[] EncodeSingle(RtcpPacket packet) => packet switch
    {
        RtcpSenderReport   sr   => EncodeSr(sr),
        RtcpReceiverReport rr   => EncodeRr(rr),
        RtcpSdesPacket     sdes => EncodeSdes(sdes),
        RtcpByePacket      bye  => EncodeBye(bye),
        _ => throw new NotSupportedException($"Cannot encode RTCP packet type {packet.Type}."),
    };

    // -------------------------------------------------------------------------
    // Encode — individual packet types
    // -------------------------------------------------------------------------

    private static byte[] EncodeSr(RtcpSenderReport sr)
    {
        var rc      = sr.ReportBlocks.Count;
        var total   = 4 + 4 + 20 + rc * 24;   // header + SSRC + sender-info + blocks
        var buf     = new byte[total];

        buf[0] = (byte)(0x80 | (rc & 0x1F));
        buf[1] = (byte)RtcpPacketType.SenderReport;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2), (ushort)(total / 4 - 1));

        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4), sr.Ssrc);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(8),  (uint)(sr.NtpTimestamp >> 32));
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(12), (uint)(sr.NtpTimestamp & 0xFFFFFFFF));
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(16), sr.RtpTimestamp);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(20), sr.SenderPacketCount);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(24), sr.SenderOctetCount);

        WriteReportBlocks(buf.AsSpan(28), sr.ReportBlocks);
        return buf;
    }

    private static byte[] EncodeRr(RtcpReceiverReport rr)
    {
        var rc    = rr.ReportBlocks.Count;
        var total = 4 + 4 + rc * 24;
        var buf   = new byte[total];

        buf[0] = (byte)(0x80 | (rc & 0x1F));
        buf[1] = (byte)RtcpPacketType.ReceiverReport;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2), (ushort)(total / 4 - 1));

        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4), rr.Ssrc);
        WriteReportBlocks(buf.AsSpan(8), rr.ReportBlocks);
        return buf;
    }

    private static void WriteReportBlocks(Span<byte> dest, IReadOnlyList<RtcpReportBlock> blocks)
    {
        for (var i = 0; i < blocks.Count; i++)
        {
            var b  = dest[(i * 24)..];
            var rb = blocks[i];
            BinaryPrimitives.WriteUInt32BigEndian(b, rb.Ssrc);

            // fraction lost + cumulative packets lost (24-bit, two's complement)
            var lost = rb.CumulativePacketsLost & 0xFFFFFF;
            b[4] = rb.FractionLost;
            b[5] = (byte)(lost >> 16);
            b[6] = (byte)(lost >> 8);
            b[7] = (byte)lost;

            BinaryPrimitives.WriteUInt32BigEndian(b[8..],  rb.ExtendedHighestSeq);
            BinaryPrimitives.WriteUInt32BigEndian(b[12..], rb.Jitter);
            BinaryPrimitives.WriteUInt32BigEndian(b[16..], rb.LastSr);
            BinaryPrimitives.WriteUInt32BigEndian(b[20..], rb.DelaySinceLastSr);
        }
    }

    private static byte[] EncodeSdes(RtcpSdesPacket sdes)
    {
        // Encode each chunk; each chunk is padded to a 4-byte boundary
        var chunkBuffers = sdes.Chunks.Select(EncodeChunk).ToArray();
        var bodyLen      = chunkBuffers.Sum(c => c.Length);
        var total        = 4 + bodyLen;
        var buf          = new byte[total];

        buf[0] = (byte)(0x80 | (sdes.Chunks.Count & 0x1F));
        buf[1] = (byte)RtcpPacketType.Sdes;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2), (ushort)(total / 4 - 1));

        var offset = 4;
        foreach (var chunk in chunkBuffers)
        {
            chunk.CopyTo(buf, offset);
            offset += chunk.Length;
        }

        return buf;
    }

    private static byte[] EncodeChunk(RtcpSdesChunk chunk)
    {
        // Calculate raw body size: SSRC(4) + items + END(1), then round up to 4 bytes
        var itemBytes = chunk.Items.Sum(item =>
        {
            var utf8 = Encoding.UTF8.GetByteCount(item.Value);
            return 1 + 1 + utf8; // type + length + value
        });

        var raw     = 4 + itemBytes + 1;  // SSRC + items + END
        var padded  = RoundUp4(raw);
        var buf     = new byte[padded];

        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0), chunk.Ssrc);
        var offset = 4;

        foreach (var item in chunk.Items)
        {
            var valueBytes = Encoding.UTF8.GetBytes(item.Value);
            buf[offset++] = (byte)item.ItemType;
            buf[offset++] = (byte)valueBytes.Length;
            valueBytes.CopyTo(buf, offset);
            offset += valueBytes.Length;
        }

        buf[offset] = (byte)RtcpSdesItemType.End; // END item; remaining bytes stay 0 (padding)
        return buf;
    }

    private static byte[] EncodeBye(RtcpByePacket bye)
    {
        var reasonBytes = bye.Reason is not null
            ? Encoding.UTF8.GetBytes(bye.Reason)
            : [];

        var raw   = 4 + bye.Sources.Count * 4
                    + (reasonBytes.Length > 0 ? 1 + reasonBytes.Length : 0);
        var total = RoundUp4(raw);
        var buf   = new byte[total];

        buf[0] = (byte)(0x80 | (bye.Sources.Count & 0x1F));
        buf[1] = (byte)RtcpPacketType.Bye;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2), (ushort)(total / 4 - 1));

        var offset = 4;
        foreach (var ssrc in bye.Sources)
        {
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(offset), ssrc);
            offset += 4;
        }

        if (reasonBytes.Length > 0)
        {
            buf[offset++] = (byte)reasonBytes.Length;
            reasonBytes.CopyTo(buf, offset);
        }

        return buf;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static int RoundUp4(int n) => (n + 3) & ~3;
}
