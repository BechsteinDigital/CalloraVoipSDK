namespace CalloraVoipSdk.Core.Infrastructure.Rtp.Packetisation;

/// <summary>
/// The VP8 RTP payload descriptor (RFC 7741 §4.2) — the bytes the sender's packetiser prepends to each
/// fragment, ahead of the encoded frame data.
/// </summary>
/// <remarks>
/// Shared by <see cref="Vp8Depacketiser"/> and <see cref="OpaqueVp8Depacketiser"/> so both read the framing
/// exactly the same way and only differ in whether they look at the frame data behind it. The descriptor is
/// generated from encoder metadata rather than parsed out of the frame, so it stays readable even when the
/// frame itself is end-to-end encrypted (the property the opaque path relies on).
/// </remarks>
internal static class Vp8PayloadDescriptor
{
    /// <summary>
    /// Measures the descriptor in <paramref name="payload"/> and reports whether the packet starts a frame.
    /// </summary>
    /// <param name="payload">One RTP payload: descriptor followed by frame data.</param>
    /// <param name="headerLength">Length of the descriptor — frame data starts here.</param>
    /// <param name="isFrameStart">
    /// <see langword="true"/> for a start-of-partition packet of partition 0 (S=1, PID=0), which opens a frame.
    /// </param>
    /// <returns>
    /// <see langword="false"/> when the descriptor is truncated or no frame data follows it — the caller
    /// discards such a payload.
    /// </returns>
    internal static bool TryStrip(ReadOnlySpan<byte> payload, out int headerLength, out bool isFrameStart)
    {
        headerLength = 0;
        isFrameStart = false;
        if (payload.Length < 2)
            return false; // descriptor plus at least one payload byte

        var b0 = payload[0];
        isFrameStart = (b0 & 0x10) != 0 && (b0 & 0x07) == 0; // S=1, PID=0
        headerLength = 1;

        if ((b0 & 0x80) == 0)
            return payload.Length > headerLength;

        if (payload.Length <= headerLength)
            return false;
        var extension = payload[headerLength++];

        if ((extension & 0x80) != 0) // I: picture ID, 15-bit form when the M bit is set
        {
            if (payload.Length <= headerLength)
                return false;
            headerLength += (payload[headerLength] & 0x80) != 0 ? 2 : 1;
        }

        if ((extension & 0x40) != 0) // L: TL0PICIDX
            headerLength++;

        if ((extension & 0x30) != 0) // T and/or K: shared TID/Y/KEYIDX byte
            headerLength++;

        return payload.Length > headerLength;
    }
}
