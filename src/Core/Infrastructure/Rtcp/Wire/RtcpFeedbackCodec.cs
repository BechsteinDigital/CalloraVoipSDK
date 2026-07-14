using System.Buffers.Binary;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;

namespace CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;

/// <summary>
/// Wire codec for RTCP feedback messages (RFC 4585 / RFC 5104): PLI and FIR (PSFB,
/// PT=206), Generic NACK (RTPFB, PT=205). The common feedback layout after the 4-byte
/// RTCP header is <c>sender SSRC(4) | media SSRC(4) | FCI(variable)</c> (RFC 4585 §6.1);
/// the feedback message type (FMT) travels in the header's low 5 bits. Kept apart from
/// <see cref="RtcpPacketCodec"/> so the base RTCP codec stays focused.
/// </summary>
internal static class RtcpFeedbackCodec
{
    private const int SsrcPairLength = 8; // sender SSRC + media SSRC

    /// <summary>
    /// Decodes a feedback packet body (past the 4-byte RTCP header). Returns
    /// <see langword="null"/> for a feedback format this SDK does not consume, so the
    /// surrounding compound packet is not discarded (RFC 3550 §6.1).
    /// </summary>
    /// <exception cref="ArgumentException">The body is truncated or malformed.</exception>
    public static RtcpPacket? Decode(RtcpPacketType type, int fmt, ReadOnlySpan<byte> body)
    {
        if (body.Length < SsrcPairLength)
            throw new ArgumentException("RTCP feedback packet too short for the SSRC pair.");

        var senderSsrc = BinaryPrimitives.ReadUInt32BigEndian(body);
        var mediaSsrc = BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
        var fci = body[SsrcPairLength..];

        return (type, fmt) switch
        {
            (RtcpPacketType.PayloadFeedback, RtcpPictureLossIndication.FeedbackMessageType)
                => new RtcpPictureLossIndication { SenderSsrc = senderSsrc, MediaSsrc = mediaSsrc },
            (RtcpPacketType.PayloadFeedback, RtcpFullIntraRequest.FeedbackMessageType)
                => DecodeFir(senderSsrc, fci),
            (RtcpPacketType.TransportFeedback, RtcpGenericNack.FeedbackMessageType)
                => DecodeNack(senderSsrc, mediaSsrc, fci),
            _ => null,
        };
    }

    /// <summary>
    /// Encodes a feedback packet, or returns <see langword="null"/> when the packet is not
    /// a feedback type this codec handles (so the caller can fall through to other types).
    /// </summary>
    public static byte[]? Encode(RtcpPacket packet) => packet switch
    {
        RtcpPictureLossIndication pli => EncodePli(pli),
        RtcpFullIntraRequest fir => EncodeFir(fir),
        RtcpGenericNack nack => EncodeNack(nack),
        _ => null,
    };

    // FIR FCI (RFC 5104 §4.3.1): per entry SSRC(4) + SeqNr(1) + Reserved(3).
    private static RtcpFullIntraRequest DecodeFir(uint senderSsrc, ReadOnlySpan<byte> fci)
    {
        const int entryLength = 8;
        if (fci.Length < entryLength || fci.Length % entryLength != 0)
            throw new ArgumentException("Malformed FIR FCI length.");

        var entries = new RtcpFirEntry[fci.Length / entryLength];
        for (var i = 0; i < entries.Length; i++)
        {
            var e = fci[(i * entryLength)..];
            entries[i] = new RtcpFirEntry
            {
                MediaSsrc = BinaryPrimitives.ReadUInt32BigEndian(e),
                SequenceNumber = e[4],
            };
        }

        return new RtcpFullIntraRequest { SenderSsrc = senderSsrc, Entries = entries };
    }

    // NACK FCI (RFC 4585 §6.2.1): per entry PID(2) + BLP(2).
    private static RtcpGenericNack DecodeNack(uint senderSsrc, uint mediaSsrc, ReadOnlySpan<byte> fci)
    {
        const int entryLength = 4;
        if (fci.Length < entryLength || fci.Length % entryLength != 0)
            throw new ArgumentException("Malformed Generic NACK FCI length.");

        var entries = new RtcpNackEntry[fci.Length / entryLength];
        for (var i = 0; i < entries.Length; i++)
        {
            var e = fci[(i * entryLength)..];
            entries[i] = new RtcpNackEntry
            {
                PacketId = BinaryPrimitives.ReadUInt16BigEndian(e),
                LostPacketBitmask = BinaryPrimitives.ReadUInt16BigEndian(e[2..]),
            };
        }

        return new RtcpGenericNack { SenderSsrc = senderSsrc, MediaSsrc = mediaSsrc, Entries = entries };
    }

    private static byte[] EncodePli(RtcpPictureLossIndication pli)
    {
        var buf = new byte[4 + SsrcPairLength];
        WriteFeedbackHeader(buf, RtcpPacketType.PayloadFeedback, RtcpPictureLossIndication.FeedbackMessageType,
            pli.SenderSsrc, pli.MediaSsrc);
        return buf;
    }

    private static byte[] EncodeFir(RtcpFullIntraRequest fir)
    {
        if (fir.Entries.Count == 0)
            throw new ArgumentException("FIR must carry at least one entry.", nameof(fir));

        const int entryLength = 8;
        var buf = new byte[4 + SsrcPairLength + fir.Entries.Count * entryLength];
        // RFC 5104 §4.3.1: the media-source SSRC in the common header is 0 for FIR; the
        // real targets live in the FCI entries.
        WriteFeedbackHeader(buf, RtcpPacketType.PayloadFeedback, RtcpFullIntraRequest.FeedbackMessageType,
            fir.SenderSsrc, mediaSsrc: 0);

        var offset = 4 + SsrcPairLength;
        foreach (var entry in fir.Entries)
        {
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(offset), entry.MediaSsrc);
            buf[offset + 4] = entry.SequenceNumber; // bytes 5..7 stay zero (reserved)
            offset += entryLength;
        }

        return buf;
    }

    private static byte[] EncodeNack(RtcpGenericNack nack)
    {
        if (nack.Entries.Count == 0)
            throw new ArgumentException("Generic NACK must carry at least one entry.", nameof(nack));

        const int entryLength = 4;
        var buf = new byte[4 + SsrcPairLength + nack.Entries.Count * entryLength];
        WriteFeedbackHeader(buf, RtcpPacketType.TransportFeedback, RtcpGenericNack.FeedbackMessageType,
            nack.SenderSsrc, nack.MediaSsrc);

        var offset = 4 + SsrcPairLength;
        foreach (var entry in nack.Entries)
        {
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(offset), entry.PacketId);
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(offset + 2), entry.LostPacketBitmask);
            offset += entryLength;
        }

        return buf;
    }

    private static void WriteFeedbackHeader(
        byte[] buf, RtcpPacketType type, int fmt, uint senderSsrc, uint mediaSsrc)
    {
        buf[0] = (byte)(0x80 | (fmt & 0x1F)); // V=2, P=0, FMT in RC field
        buf[1] = (byte)type;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2), (ushort)(buf.Length / 4 - 1));
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4), senderSsrc);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(8), mediaSsrc);
    }
}
