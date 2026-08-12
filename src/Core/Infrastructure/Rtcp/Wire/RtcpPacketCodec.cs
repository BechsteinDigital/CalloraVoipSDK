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
    // RFC 3550 §6.1 wire-DoS budget (rule K4): a legitimate compound is a handful of
    // sub-packets (SR/RR + SDES + a little feedback + BYE), far below this. Bounding the
    // sub-packet count before decode denies a peer the ability to force unbounded objects
    // and work with a datagram full of minimal 8-byte sub-packets.
    internal const int MaxPacketsPerCompound = 32;

    // Wire-DoS byte budget (rule K4): a compliant RTCP compound stays within the path MTU
    // (RFC 3550 §6.2), so an oversized datagram is rejected before any sub-packet is touched.
    // 1500 bytes (Ethernet MTU) leaves ample headroom for legitimate reports. Enforced here at
    // the shared decode boundary so every caller — the dedicated RTCP socket, BUNDLE, and the
    // SIP path — inherits the same cap without duplicating the check.
    internal const int MaxRtcpDatagramBytes = 1500;

    // -------------------------------------------------------------------------
    // Decode
    // -------------------------------------------------------------------------

    public IReadOnlyList<RtcpPacket> Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            throw new ArgumentException("RTCP compound packet is empty.", nameof(data));

        // Wire-DoS byte budget (rule K4): reject an oversized compound before decoding any
        // sub-packet, so a peer cannot force decode work with a jumbo datagram (MaxRtcpDatagramBytes).
        if (data.Length > MaxRtcpDatagramBytes)
            throw new ArgumentException(
                $"RTCP compound of {data.Length} bytes exceeds the {MaxRtcpDatagramBytes}-byte budget.",
                nameof(data));

        var packets = new List<RtcpPacket>();
        var offset  = 0;
        var packetCount = 0;

        while (offset < data.Length)
        {
            // Wire-DoS budget (rule K4): reject before touching a sub-packet beyond the cap, so
            // the object/work count stays bounded no matter the datagram size (MaxPacketsPerCompound).
            if (++packetCount > MaxPacketsPerCompound)
                throw new ArgumentException(
                    $"RTCP compound exceeds the {MaxPacketsPerCompound}-sub-packet budget.");

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

            // RFC 3550 §6.1: still-unrecognized packet types are skipped via the length field —
            // throwing here would discard the whole compound datagram including the SR/RR it
            // starts with.
            RtcpPacket? packet = pt switch
            {
                RtcpPacketType.SenderReport   => DecodeSr(body, count),
                RtcpPacketType.ReceiverReport => DecodeRr(body, count),
                RtcpPacketType.Sdes           => DecodeSdes(body, count),
                RtcpPacketType.Bye            => DecodeBye(body, count),
                RtcpPacketType.ExtendedReport => DecodeXr(body),
                // Feedback (RFC 4585/5104): the low 5 header bits carry the FMT, not an RC.
                RtcpPacketType.TransportFeedback or RtcpPacketType.PayloadFeedback
                    => DecodeFeedbackTolerant(pt, count, body),
                _ => null,
            };

            if (packet is not null)
                packets.Add(packet);
            offset += packetLen;
        }

        return packets;
    }

    // RFC 3550 §6.1: a malformed feedback packet must not discard the surrounding compound
    // (typically the SR/RR it starts with). The feedback sub-codecs validate strictly and throw
    // on a truncated or inconsistent FCI; at the compound layer that means "skip just this packet"
    // (its length field already advanced the read offset), not "drop the whole datagram". Scoped to
    // ArgumentException so a genuine decoder bug (e.g. IndexOutOfRange) still surfaces.
    private static RtcpPacket? DecodeFeedbackTolerant(RtcpPacketType pt, int fmt, ReadOnlySpan<byte> body)
    {
        try
        {
            return RtcpFeedbackCodec.Decode(pt, fmt, body);
        }
        catch (ArgumentException)
        {
            return null;
        }
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
                    // Skip padding to the next 4-byte boundary.
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

    // RFC 3611 §2: XR body = SSRC(4) followed by typed report blocks. Each block is
    // BT(1) + type-specific(1) + block-length(2, in 32-bit words of block content) + content.
    // Unknown block types are skipped via block-length; VoIP Metrics blocks (BT=7, §4.7) are
    // parsed. Malformed block lengths stop parsing rather than throwing, so a bad XR block does
    // not discard the surrounding compound packet.
    private static RtcpExtendedReport DecodeXr(ReadOnlySpan<byte> body)
    {
        if (body.Length < 4)
            throw new ArgumentException("XR body too short for SSRC.");

        var ssrc = BinaryPrimitives.ReadUInt32BigEndian(body);
        var metrics = new List<RtcpVoipMetricsBlock>();

        var offset = 4;
        while (offset + 4 <= body.Length)
        {
            var blockType    = body[offset];
            var blockWords   = BinaryPrimitives.ReadUInt16BigEndian(body[(offset + 2)..]);
            var contentBytes = blockWords * 4;
            var contentStart = offset + 4;
            if (contentStart + contentBytes > body.Length)
                break; // truncated / inconsistent block length

            if (blockType == VoipMetricsBlockType && contentBytes >= VoipMetricsContentBytes)
                metrics.Add(DecodeVoipMetrics(body.Slice(contentStart, VoipMetricsContentBytes)));

            offset = contentStart + contentBytes;
        }

        return new RtcpExtendedReport { Ssrc = ssrc, VoipMetrics = metrics };
    }

    private const byte VoipMetricsBlockType = 7;
    private const int VoipMetricsContentBytes = 32;

    private static RtcpVoipMetricsBlock DecodeVoipMetrics(ReadOnlySpan<byte> c) => new()
    {
        SourceSsrc                = BinaryPrimitives.ReadUInt32BigEndian(c),
        LossRate                  = c[4],
        DiscardRate               = c[5],
        BurstDensity              = c[6],
        GapDensity                = c[7],
        BurstDurationMs           = BinaryPrimitives.ReadUInt16BigEndian(c[8..]),
        GapDurationMs             = BinaryPrimitives.ReadUInt16BigEndian(c[10..]),
        RoundTripDelayMs          = BinaryPrimitives.ReadUInt16BigEndian(c[12..]),
        EndSystemDelayMs          = BinaryPrimitives.ReadUInt16BigEndian(c[14..]),
        RFactor                   = c[20],
        ExternalRFactor           = c[21],
        MosLq                     = c[22],
        MosCq                     = c[23],
        JitterBufferNominalMs     = BinaryPrimitives.ReadUInt16BigEndian(c[26..]),
        JitterBufferMaximumMs     = BinaryPrimitives.ReadUInt16BigEndian(c[28..]),
        JitterBufferAbsoluteMaxMs = BinaryPrimitives.ReadUInt16BigEndian(c[30..]),
    };

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
        _ => RtcpFeedbackCodec.Encode(packet)
             ?? throw new NotSupportedException($"Cannot encode RTCP packet type {packet.Type}."),
    };

    // -------------------------------------------------------------------------
    // Encode — individual packet types
    // -------------------------------------------------------------------------

    // The RC/SC header field is 5 bits (RFC 3550 §6.1), so at most 31 items fit in one packet.
    private const int MaxHeaderCount = 31;

    // #162 P2-2: validate rather than mask. Masking the count with & 0x1F while still writing every item
    // to the body made the packet contradict itself at 32 items — RC/SC wrapped to 0 while the body
    // carried 32 entries, so our own encode no longer round-tripped through our own decode (32 blocks in,
    // 0 blocks out). A caller with more than 31 items must page them across several packets, as
    // BundledRtcpReporter already does (RFC 3550 §6.1/§6.4.1); failing loudly here is what makes an
    // unpaged caller visible instead of silently emitting a corrupt packet.
    private static int ValidateCount(int count, string what)
    {
        if ((uint)count > MaxHeaderCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count), count,
                $"RTCP {what}: at most {MaxHeaderCount} fit in one packet (5-bit RC/SC field, RFC 3550 §6.1); page across multiple packets.");
        }

        return count;
    }

    // The header length field counts 32-bit words minus one and is 16 bits wide (RFC 3550 §6.4.1). The
    // count check above bounds SR/RR, but an SDES packet's size is driven by its item values, so its
    // length is checked independently rather than truncated into the field.
    private static ushort LengthWords(int totalBytes, string what)
    {
        var words = totalBytes / 4 - 1;
        if ((uint)words > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(totalBytes), totalBytes,
                $"RTCP {what}: encoded length {totalBytes} bytes exceeds the 16-bit header length field (RFC 3550 §6.4.1).");
        }

        return (ushort)words;
    }

    private static byte[] EncodeSr(RtcpSenderReport sr)
    {
        var rc      = ValidateCount(sr.ReportBlocks.Count, "SR reception report blocks");
        var total   = 4 + 4 + 20 + rc * 24;   // header + SSRC + sender-info + blocks
        var buf     = new byte[total];

        buf[0] = (byte)(0x80 | rc);
        buf[1] = (byte)RtcpPacketType.SenderReport;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2), LengthWords(total, "SR"));

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
        var rc    = ValidateCount(rr.ReportBlocks.Count, "RR reception report blocks");
        var total = 4 + 4 + rc * 24;
        var buf   = new byte[total];

        buf[0] = (byte)(0x80 | rc);
        buf[1] = (byte)RtcpPacketType.ReceiverReport;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2), LengthWords(total, "RR"));

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

        buf[0] = (byte)(0x80 | ValidateCount(sdes.Chunks.Count, "SDES chunks"));
        buf[1] = (byte)RtcpPacketType.Sdes;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2), LengthWords(total, "SDES"));

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
        // Each SDES item value is length-prefixed by a single byte, so it cannot exceed 255 bytes (RFC 3550
        // §6.5). Clamp the UTF-8 value once and reuse it for both sizing and writing, so a value > 255 bytes is
        // truncated rather than emitting a wrapped length byte that no longer matches the written content.
        var items = chunk.Items
            .Select(item => (item.ItemType, Value: ClampToByteLength(item.Value)))
            .ToArray();

        var itemBytes = items.Sum(item => 1 + 1 + item.Value.Length); // type + length + value

        var raw     = 4 + itemBytes + 1;  // SSRC + items + END
        var padded  = RoundUp4(raw);
        var buf     = new byte[padded];

        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0), chunk.Ssrc);
        var offset = 4;

        foreach (var (itemType, valueBytes) in items)
        {
            buf[offset++] = (byte)itemType;
            buf[offset++] = (byte)valueBytes.Length;
            valueBytes.CopyTo(buf, offset);
            offset += valueBytes.Length;
        }

        buf[offset] = (byte)RtcpSdesItemType.End; // END item; remaining bytes stay 0 (padding)
        return buf;
    }

    private static byte[] EncodeBye(RtcpByePacket bye)
    {
        // The BYE reason is length-prefixed by a single byte (RFC 3550 §6.6), so clamp it to 255 bytes rather
        // than emitting a wrapped length byte that would corrupt the packet.
        var reasonBytes = bye.Reason is not null
            ? ClampToByteLength(bye.Reason)
            : [];

        var raw   = 4 + bye.Sources.Count * 4
                    + (reasonBytes.Length > 0 ? 1 + reasonBytes.Length : 0);
        var total = RoundUp4(raw);
        var buf   = new byte[total];

        buf[0] = (byte)(0x80 | ValidateCount(bye.Sources.Count, "BYE sources"));
        buf[1] = (byte)RtcpPacketType.Bye;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2), LengthWords(total, "BYE"));

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

    /// <summary>
    /// Encodes a string as UTF-8 and clamps it to at most 255 bytes — the maximum a single-byte RTCP length
    /// prefix (SDES item value, BYE reason) can represent (RFC 3550 §6.5/§6.6). A pathologically long value is
    /// truncated at a UTF-8 codepoint boundary (never mid-codepoint) so the emitted length byte matches the
    /// written content and the value stays valid UTF-8.
    /// </summary>
    private static byte[] ClampToByteLength(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= 255)
            return bytes;

        // Walk back from the 255-byte limit past any UTF-8 continuation bytes (10xxxxxx) so the cut lands on a
        // codepoint boundary rather than splitting a multi-byte character.
        var length = 255;
        while (length > 0 && (bytes[length] & 0xC0) == 0x80)
            length--;
        return bytes[..length];
    }
}
