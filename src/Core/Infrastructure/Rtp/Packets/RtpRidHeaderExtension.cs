using System.Text;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;

/// <summary>
/// Codec for the RTP stream identification (RID) SDES header extension (RFC 8852 / RFC 8851) in the
/// RFC 8285 one-byte form. It carries an encoding's <c>a=rid</c> id (for example <c>hi</c>, <c>mid</c>,
/// <c>lo</c>) per packet so a receiver can associate a simulcast stream's SSRC with its <c>a=rid</c>
/// encoding (RFC 8853) — the per-encoding counterpart to the MID extension, which routes to the m-line.
/// The value is the ASCII RID token, 1..16 bytes in the one-byte form; a longer id would require the
/// two-byte form and is rejected here. Reuses the shared one-byte constants and wire layout of
/// <see cref="OneByteRtpHeaderExtensions"/>, mirroring <see cref="RtpMidHeaderExtension"/>.
/// </summary>
internal static class RtpRidHeaderExtension
{
    /// <summary>
    /// Builds the RID element for the negotiated <paramref name="id"/>, to combine with other
    /// header-extension elements (MID, transport-cc) via <see cref="OneByteRtpHeaderExtensions.Encode"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The RID is empty or exceeds 16 bytes (one-byte form limit).</exception>
    public static RtpHeaderExtensionElement Element(byte id, string rid) =>
        new(id, EncodeValue(rid));

    /// <summary>
    /// Builds a <c>0xBEDE</c> extension carrying only the RID, writing the wire bytes directly — the
    /// allocation-lean single-extension path. The bytes are identical to
    /// <c>OneByteRtpHeaderExtensions.Encode([Element(id, rid)])</c>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> is out of the 1..14 range, or the RID is empty / exceeds 16 bytes.
    /// </exception>
    public static RtpExtension Encode(byte id, string rid)
    {
        if (id is < OneByteRtpHeaderExtensions.MinId or > OneByteRtpHeaderExtensions.MaxId)
            throw new ArgumentException(
                $"One-byte header-extension id {id} is out of range " +
                $"(must be {OneByteRtpHeaderExtensions.MinId}..{OneByteRtpHeaderExtensions.MaxId}).",
                nameof(id));

        var value = EncodeValue(rid);
        var padded = (1 + value.Length + 3) & ~3; // header(1) + value, rounded up to a 32-bit boundary
        var data = new byte[padded];
        data[0] = (byte)((id << 4) | (value.Length - 1)); // id in the high nibble, length-1 in the low
        value.CopyTo(data.AsSpan(1));
        // Trailing bytes stay zero — RFC 8285 padding.
        return new RtpExtension { Profile = OneByteRtpHeaderExtensions.Profile, Data = data };
    }

    /// <summary>
    /// Reads the RID carried under the negotiated <paramref name="id"/> from an inbound packet's header
    /// extension. Allocation-free scan (same lenient RFC 8285 rules as
    /// <see cref="OneByteRtpHeaderExtensions"/>: skip padding, stop at id 15, tolerate a truncated tail)
    /// with an early exit on the matched id. Returns <see langword="false"/> when the extension is
    /// absent, is not the <c>0xBEDE</c> profile, or carries no element with that id.
    /// </summary>
    public static bool TryRead(RtpExtension? extension, byte id, out string rid)
    {
        rid = string.Empty;
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
                rid = Encoding.ASCII.GetString(data.Slice(offset, length));
                return true;
            }

            offset += length;
        }

        return false;
    }

    private static byte[] EncodeValue(string rid)
    {
        ArgumentException.ThrowIfNullOrEmpty(rid);

        var value = Encoding.ASCII.GetBytes(rid);
        if (value.Length > OneByteRtpHeaderExtensions.MaxValueLength)
            throw new ArgumentException(
                $"RID '{rid}' is {value.Length} bytes; the one-byte header-extension form allows at most " +
                $"{OneByteRtpHeaderExtensions.MaxValueLength} (a longer RID needs the two-byte form).",
                nameof(rid));

        return value;
    }
}
