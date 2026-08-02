using System.Buffers.Binary;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Turn.Wire;

/// <summary>
/// Reads TURN-over-TCP/TLS frames, supporting both STUN messages and ChannelData packets.
/// </summary>
internal static class TurnStreamFramer
{
    // A STUN control message framed over TURN-TCP/TLS is small; nothing legitimate approaches the
    // 64 KiB the 16-bit length field could claim. Validate the declared length against this ceiling
    // before allocating the frame buffer, so a single 4-byte header cannot force a large speculative
    // allocation (memory DoS). ChannelData is bounded separately — see ReadFrameAsync.
    private const int MaxStunFrameBodyBytes = 16 * 1024;

    /// <summary>
    /// Reads one full frame from the stream.
    /// Returns null on clean EOF.
    /// </summary>
    public static async Task<TurnStreamFrame?> ReadFrameAsync(Stream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var header = new byte[4];
        int headerRead = await ReadExactAsync(stream, header, ct).ConfigureAwait(false);
        if (headerRead == 0)
            return null;
        if (headerRead < 4)
            throw new InvalidDataException($"TURN stream closed after {headerRead} header bytes; expected 4.");

        ushort first = BinaryPrimitives.ReadUInt16BigEndian(header);

        if (first >= 0x4000 && first <= 0x7FFF)
        {
            // ChannelData carries a relayed peer datagram, which the server itself can legitimately
            // produce up to the 16-bit length maximum (~64 KiB). It is already bounded by that field,
            // so no tighter cap is applied here — only the STUN control path is capped.
            ushort channelLength = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
            var payload = new byte[channelLength];
            int payloadRead = await ReadExactAsync(stream, payload, ct).ConfigureAwait(false);
            if (payloadRead < channelLength)
                throw new InvalidDataException($"TURN stream closed after {payloadRead} channel bytes; expected {channelLength}.");

            // RFC 8656 §12.5: over a stream, ChannelData is padded to a 4-byte boundary with 0-3 bytes that are
            // not counted in the length field. Consume them so the next framed message starts aligned (STUN
            // frames are already 4-byte aligned by RFC 8489 §5, so only ChannelData needs this).
            int padding = (4 - (channelLength & 3)) & 3;
            if (padding > 0)
            {
                var pad = new byte[padding];
                int padRead = await ReadExactAsync(stream, pad, ct).ConfigureAwait(false);
                if (padRead < padding)
                    throw new InvalidDataException($"TURN stream closed after {padRead} channel pad bytes; expected {padding}.");
            }

            return new TurnStreamFrame
            {
                IsChannelData = true,
                ChannelNumber = first,
                Payload = payload
            };
        }

        ushort stunLength = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
        if (stunLength > MaxStunFrameBodyBytes)
            throw new InvalidDataException($"TURN STUN frame body {stunLength} exceeds the {MaxStunFrameBodyBytes}-byte limit.");

        var packet = new byte[StunWireConstants.HeaderSize + stunLength];
        header.CopyTo(packet, 0);

        int remainder = packet.Length - 4;
        int remainderRead = await ReadExactAsync(stream, packet.AsMemory(4), ct).ConfigureAwait(false);
        if (remainderRead < remainder)
            throw new InvalidDataException($"TURN stream closed after {remainderRead} STUN bytes; expected {remainder}.");

        return new TurnStreamFrame
        {
            IsChannelData = false,
            Payload = packet
        };
    }

    private static async Task<int> ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[totalRead..], ct).ConfigureAwait(false);
            if (read == 0)
                break;
            totalRead += read;
        }

        return totalRead;
    }
}
