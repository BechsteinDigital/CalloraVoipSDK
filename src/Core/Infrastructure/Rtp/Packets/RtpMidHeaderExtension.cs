using System.Text;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;

/// <summary>
/// Codec for the media identification (MID) SDES RTP header extension (RFC 9143 / RFC 8843 §15) in the
/// RFC 8285 one-byte form. It carries the m-line's <c>a=mid</c> token (for example <c>0</c>, <c>audio</c>)
/// per packet so a BUNDLE receiver can route an inbound packet to the right media section before an SSRC
/// is latched — the routing foundation for the bundled transport (ADR-010 B1). The value is the ASCII
/// MID token, 1..16 bytes in the one-byte form; a longer MID would require the two-byte form and is
/// rejected here. Reuses the shared one-byte constants and wire layout of
/// <see cref="OneByteRtpHeaderExtensions"/>.
/// </summary>
internal static class RtpMidHeaderExtension
{
    /// <summary>
    /// Builds the MID element for the negotiated <paramref name="id"/>, to combine with other
    /// header-extension elements (for example transport-cc) via <see cref="OneByteRtpHeaderExtensions.Encode"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The MID is empty or exceeds 16 bytes (one-byte form limit).</exception>
    public static RtpHeaderExtensionElement Element(byte id, string mid) =>
        new(id, EncodeValue(mid));

    /// <summary>
    /// Builds a <c>0xBEDE</c> extension carrying only the MID, writing the wire bytes directly — the
    /// allocation-lean single-extension path. The bytes are identical to
    /// <c>OneByteRtpHeaderExtensions.Encode([Element(id, mid)])</c>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> is out of the 1..14 range, or the MID is empty / exceeds 16 bytes.
    /// </exception>
    public static RtpExtension Encode(byte id, string mid)
    {
        if (id is < OneByteRtpHeaderExtensions.MinId or > OneByteRtpHeaderExtensions.MaxId)
            throw new ArgumentException(
                $"One-byte header-extension id {id} is out of range " +
                $"(must be {OneByteRtpHeaderExtensions.MinId}..{OneByteRtpHeaderExtensions.MaxId}).",
                nameof(id));

        var value = EncodeValue(mid);
        var padded = (1 + value.Length + 3) & ~3; // header(1) + value, rounded up to a 32-bit boundary
        var data = new byte[padded];
        data[0] = (byte)((id << 4) | (value.Length - 1)); // id in the high nibble, length-1 in the low
        value.CopyTo(data.AsSpan(1));
        // Trailing bytes stay zero — RFC 8285 padding.
        return new RtpExtension { Profile = OneByteRtpHeaderExtensions.Profile, Data = data };
    }

    /// <summary>
    /// Reads the MID carried under the negotiated <paramref name="id"/> from an inbound packet's header
    /// extension. Allocation-free scan (same lenient RFC 8285 rules as
    /// <see cref="OneByteRtpHeaderExtensions"/>: skip padding, stop at id 15, tolerate a truncated tail)
    /// with an early exit on the matched id. Returns <see langword="false"/> when the extension is
    /// absent, is not the <c>0xBEDE</c> profile, or carries no element with that id.
    /// </summary>
    public static bool TryRead(RtpExtension? extension, byte id, out string mid)
    {
        mid = string.Empty;
        if (extension is null || extension.Profile != OneByteRtpHeaderExtensions.Profile)
            return false;

        var data = extension.Data.Span;
        var offset = 0;
        while (offset < data.Length)
        {
            var header = data[offset];
            if (header == 0) // padding
            {
                offset++;
                continue;
            }

            var elementId = (byte)(header >> 4);
            if (elementId == 15) // reserved: stop parsing
                break;

            var length = (header & 0x0F) + 1;
            offset++;
            if (offset + length > data.Length) // truncated trailing element
                break;

            if (elementId == id)
            {
                mid = Encoding.ASCII.GetString(data.Slice(offset, length));
                return true;
            }

            offset += length;
        }

        return false;
    }

    private static byte[] EncodeValue(string mid)
    {
        ArgumentException.ThrowIfNullOrEmpty(mid);

        var value = Encoding.ASCII.GetBytes(mid);
        if (value.Length > OneByteRtpHeaderExtensions.MaxValueLength)
            throw new ArgumentException(
                $"MID '{mid}' is {value.Length} bytes; the one-byte header-extension form allows at most " +
                $"{OneByteRtpHeaderExtensions.MaxValueLength} (a longer MID needs the two-byte form).",
                nameof(mid));

        return value;
    }
}
