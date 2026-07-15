using System.Buffers.Binary;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;

namespace CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;

/// <summary>
/// Wire codec for transport-wide congestion-control feedback (RTPFB PT=205, FMT=15 —
/// draft-holmer-rmcat-transport-wide-cc-extensions-01 §3.1). The message body after the SSRC
/// pair is <c>base seq(2) | packet status count(2) | reference time(3) | fb pkt count(1)</c>
/// followed by packet-status chunks and then the receive deltas, zero-padded to a 32-bit
/// boundary. Kept apart from <see cref="RtcpFeedbackCodec"/> because the chunk/delta packing is
/// substantially more involved than the fixed-layout NACK/PLI/FIR feedback.
/// </summary>
/// <remarks>
/// Encoding emits two-bit status-vector chunks (one chunk per seven packets); run-length chunks
/// are a valid, more compact representation this encoder does not yet produce (decoding accepts
/// all chunk forms peers send). This is a pure wire codec: it is not yet wired into the RTCP
/// receive/dispatch path, so it changes no runtime behaviour on its own.
/// </remarks>
internal static class RtcpTransportFeedbackCodec
{
    private const int SsrcPairLength = 8;     // sender SSRC + media SSRC (RFC 4585 §6.1)
    private const int HeaderFieldsLength = 8; // base seq(2) + status count(2) + reference time(3) + fb count(1)
    private const int ChunkLength = 2;

    // Packet status symbols (draft §3.1.1).
    private const int NotReceived = 0;
    private const int ReceivedSmallDelta = 1;
    private const int ReceivedLargeDelta = 2;

    private const int MaxReferenceTime = 0x7FFFFF;   // signed 24-bit
    private const int MinReferenceTime = -0x800000;
    private const int SmallDeltaMax = 0xFF;          // one unsigned byte (250 µs ticks)
    private const int SymbolsPerVectorChunk = 7;     // two-bit symbols in a status-vector chunk

    /// <summary>
    /// Encodes a full transport-cc feedback RTCP packet (header + SSRC pair + body), zero-padded to
    /// a 32-bit boundary.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The status list is empty, a delta or the reference time is out of the representable range.
    /// </exception>
    public static byte[] Encode(RtcpTransportFeedback feedback)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        var statuses = feedback.Statuses;
        if (statuses.Count == 0)
            throw new ArgumentException("Transport-cc feedback must report at least one packet.", nameof(feedback));
        if (feedback.ReferenceTimeTicks is < MinReferenceTime or > MaxReferenceTime)
            throw new ArgumentException(
                $"Reference time {feedback.ReferenceTimeTicks} is out of the signed 24-bit range.", nameof(feedback));

        var symbols = new int[statuses.Count];
        var deltaBytes = 0;
        for (var i = 0; i < statuses.Count; i++)
        {
            var status = statuses[i];
            if (!status.Received)
            {
                symbols[i] = NotReceived;
                continue;
            }

            if (status.DeltaTicks is >= 0 and <= SmallDeltaMax)
            {
                symbols[i] = ReceivedSmallDelta;
                deltaBytes += 1;
            }
            else if (status.DeltaTicks is >= short.MinValue and <= short.MaxValue)
            {
                symbols[i] = ReceivedLargeDelta;
                deltaBytes += 2;
            }
            else
            {
                throw new ArgumentException(
                    $"Arrival delta {status.DeltaTicks} ticks is out of the two-byte range for sequence " +
                    $"{status.SequenceNumber}.", nameof(feedback));
            }
        }

        var chunkCount = (statuses.Count + SymbolsPerVectorChunk - 1) / SymbolsPerVectorChunk;
        var unpadded = 4 + SsrcPairLength + HeaderFieldsLength + chunkCount * ChunkLength + deltaBytes;
        var padded = (unpadded + 3) & ~3;
        var buffer = new byte[padded];
        var span = buffer.AsSpan();

        // RTCP common header (V=2, P=0, FMT=15) + SSRC pair (draft §3.1 / RFC 4585 §6.1).
        buffer[0] = 0x80 | (RtcpTransportFeedback.FeedbackMessageType & 0x1F);
        buffer[1] = (byte)RtcpPacketType.TransportFeedback;
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], (ushort)(padded / 4 - 1));
        BinaryPrimitives.WriteUInt32BigEndian(span[4..], feedback.SenderSsrc);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..], feedback.MediaSsrc);

        var offset = 4 + SsrcPairLength;
        BinaryPrimitives.WriteUInt16BigEndian(span[offset..], statuses[0].SequenceNumber);
        BinaryPrimitives.WriteUInt16BigEndian(span[(offset + 2)..], (ushort)statuses.Count);
        var referenceTime = feedback.ReferenceTimeTicks;
        buffer[offset + 4] = (byte)((referenceTime >> 16) & 0xFF);
        buffer[offset + 5] = (byte)((referenceTime >> 8) & 0xFF);
        buffer[offset + 6] = (byte)(referenceTime & 0xFF);
        buffer[offset + 7] = feedback.FeedbackPacketCount;
        offset += HeaderFieldsLength;

        // Packet-status chunks: two-bit status-vector chunks, seven symbols each (draft §3.1.4).
        for (var chunk = 0; chunk < chunkCount; chunk++)
        {
            var value = 0xC000; // T=1 (status vector), S=1 (two-bit symbols)
            for (var slot = 0; slot < SymbolsPerVectorChunk; slot++)
            {
                var index = chunk * SymbolsPerVectorChunk + slot;
                var symbol = index < symbols.Length ? symbols[index] : NotReceived;
                value |= symbol << (12 - 2 * slot);
            }

            BinaryPrimitives.WriteUInt16BigEndian(span[offset..], (ushort)value);
            offset += ChunkLength;
        }

        // Receive deltas in packet order (draft §3.1.5); not-received packets contribute none.
        for (var i = 0; i < statuses.Count; i++)
        {
            switch (symbols[i])
            {
                case ReceivedSmallDelta:
                    buffer[offset++] = (byte)statuses[i].DeltaTicks;
                    break;
                case ReceivedLargeDelta:
                    BinaryPrimitives.WriteInt16BigEndian(span[offset..], (short)statuses[i].DeltaTicks);
                    offset += 2;
                    break;
            }
        }

        // Bytes past offset stay zero — the 32-bit padding the body is rounded up to.
        return buffer;
    }

    /// <summary>
    /// Decodes a transport-cc feedback body (the bytes after the sender/media SSRC pair) with the
    /// SSRCs already parsed from the common feedback header. Accepts run-length and one/two-bit
    /// status-vector chunks; trailing zero padding is ignored.
    /// </summary>
    /// <exception cref="ArgumentException">The body is truncated or carries a reserved symbol.</exception>
    public static RtcpTransportFeedback Decode(uint senderSsrc, uint mediaSsrc, ReadOnlySpan<byte> fci)
    {
        if (fci.Length < HeaderFieldsLength)
            throw new ArgumentException("Transport-cc feedback too short for the header fields.");

        var baseSequence = BinaryPrimitives.ReadUInt16BigEndian(fci);
        var statusCount = BinaryPrimitives.ReadUInt16BigEndian(fci[2..]);
        if (statusCount == 0)
            throw new ArgumentException("Transport-cc feedback reports zero packets.");

        var referenceTime = (fci[4] << 16) | (fci[5] << 8) | fci[6];
        if ((referenceTime & 0x800000) != 0)
            referenceTime |= unchecked((int)0xFF000000); // sign-extend the 24-bit value
        var feedbackPacketCount = fci[7];

        var symbols = new List<int>(statusCount);
        var offset = HeaderFieldsLength;
        while (symbols.Count < statusCount)
        {
            if (offset + ChunkLength > fci.Length)
                throw new ArgumentException("Transport-cc feedback truncated inside the packet-status chunks.");

            var chunk = BinaryPrimitives.ReadUInt16BigEndian(fci[offset..]);
            offset += ChunkLength;
            ReadChunkSymbols(chunk, statusCount, symbols);
        }

        var statuses = new RtcpTransportFeedbackStatus[statusCount];
        for (var i = 0; i < statusCount; i++)
        {
            var symbol = symbols[i];
            var received = symbol is ReceivedSmallDelta or ReceivedLargeDelta;
            var delta = 0;
            switch (symbol)
            {
                case NotReceived:
                    break;
                case ReceivedSmallDelta:
                    if (offset + 1 > fci.Length)
                        throw new ArgumentException("Transport-cc feedback truncated inside the receive deltas.");
                    delta = fci[offset++];
                    break;
                case ReceivedLargeDelta:
                    if (offset + 2 > fci.Length)
                        throw new ArgumentException("Transport-cc feedback truncated inside the receive deltas.");
                    delta = BinaryPrimitives.ReadInt16BigEndian(fci[offset..]);
                    offset += 2;
                    break;
                default:
                    throw new ArgumentException($"Transport-cc feedback carries reserved status symbol {symbol}.");
            }

            statuses[i] = new RtcpTransportFeedbackStatus
            {
                SequenceNumber = unchecked((ushort)(baseSequence + i)),
                Received = received,
                DeltaTicks = delta,
            };
        }

        return new RtcpTransportFeedback
        {
            SenderSsrc = senderSsrc,
            MediaSsrc = mediaSsrc,
            ReferenceTimeTicks = referenceTime,
            FeedbackPacketCount = feedbackPacketCount,
            Statuses = statuses,
        };
    }

    // Appends the symbols carried by one chunk (draft §3.1.2–3.1.4), stopping once statusCount is met.
    private static void ReadChunkSymbols(ushort chunk, int statusCount, List<int> symbols)
    {
        if ((chunk & 0x8000) == 0)
        {
            // Run-length chunk: symbol(2) repeated run_length(13) times.
            var symbol = (chunk >> 13) & 0x3;
            var run = chunk & 0x1FFF;
            for (var i = 0; i < run && symbols.Count < statusCount; i++)
                symbols.Add(symbol);
            return;
        }

        if ((chunk & 0x4000) == 0)
        {
            // Status-vector chunk, one-bit symbols: 0 = not received, 1 = received (small delta).
            for (var i = 0; i < 14 && symbols.Count < statusCount; i++)
                symbols.Add(((chunk >> (13 - i)) & 0x1) == 1 ? ReceivedSmallDelta : NotReceived);
            return;
        }

        // Status-vector chunk, two-bit symbols.
        for (var i = 0; i < SymbolsPerVectorChunk && symbols.Count < statusCount; i++)
            symbols.Add((chunk >> (12 - 2 * i)) & 0x3);
    }
}
