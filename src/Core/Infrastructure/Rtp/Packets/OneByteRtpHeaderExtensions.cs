namespace CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;

/// <summary>
/// Codec for the RFC 8285 one-byte header-extension form carried in an <see cref="RtpExtension"/>
/// with profile <c>0xBEDE</c>. Each element is a one-byte header (<c>ID</c> in the high nibble,
/// <c>length-1</c> in the low nibble) followed by 1..16 value bytes; a zero byte is inter-element
/// padding and identifier 15 stops parsing. This is the transport for per-packet metadata such as
/// the transport-wide sequence number (congestion control) — the SDP <c>a=extmap</c> negotiation
/// and the concrete extensions build on top of it.
/// </summary>
internal static class OneByteRtpHeaderExtensions
{
    /// <summary>The RFC 8285 one-byte profile value in the extension header.</summary>
    internal const ushort Profile = 0xBEDE;

    /// <summary>Lowest valid one-byte identifier (0 is reserved for padding).</summary>
    internal const byte MinId = 1;

    /// <summary>Highest valid one-byte identifier (15 is reserved to signal "stop parsing").</summary>
    internal const byte MaxId = 14;

    /// <summary>Maximum value length representable by the 4-bit length field (encoded as length-1).</summary>
    internal const int MaxValueLength = 16;

    /// <summary>
    /// Packs the elements into a <see cref="RtpExtension"/> (profile <c>0xBEDE</c>), zero-padded to a
    /// 32-bit boundary as the RTP extension body requires. Returns <see langword="null"/> when there
    /// is nothing to encode. Throws <see cref="ArgumentException"/> for an out-of-range identifier
    /// (must be 1..14) or value length (must be 1..16) — an invalid element is a construction bug,
    /// not silently dropped.
    /// </summary>
    public static RtpExtension? Encode(IReadOnlyList<RtpHeaderExtensionElement> elements)
    {
        ArgumentNullException.ThrowIfNull(elements);
        if (elements.Count == 0)
            return null;

        var unpadded = 0;
        foreach (var element in elements)
        {
            if (element.Id is < MinId or > MaxId)
                throw new ArgumentException(
                    $"One-byte header-extension id {element.Id} is out of range (must be {MinId}..{MaxId}).",
                    nameof(elements));
            if (element.Value.Length is < 1 or > MaxValueLength)
                throw new ArgumentException(
                    $"One-byte header-extension value length {element.Value.Length} is out of range (must be 1..{MaxValueLength}).",
                    nameof(elements));
            unpadded += 1 + element.Value.Length;
        }

        var padded = (unpadded + 3) & ~3; // round up to a multiple of 4
        var data = new byte[padded];
        var offset = 0;
        foreach (var element in elements)
        {
            data[offset++] = (byte)((element.Id << 4) | (element.Value.Length - 1));
            element.Value.Span.CopyTo(data.AsSpan(offset));
            offset += element.Value.Length;
        }
        // The remaining bytes stay zero — RFC 8285 inter/trailing padding.

        return new RtpExtension { Profile = Profile, Data = data };
    }

    /// <summary>
    /// Parses a <c>0xBEDE</c> extension into its elements, in wire order, each value copied into an
    /// owned buffer (the source may alias a reused receive buffer). A non-<c>0xBEDE</c> extension
    /// yields an empty list. Parsing is lenient on received data (RFC 8285): zero padding bytes are
    /// skipped, identifier 15 stops parsing, and a truncated trailing element is dropped — the
    /// valid prefix is still returned.
    /// </summary>
    public static IReadOnlyList<RtpHeaderExtensionElement> Parse(RtpExtension extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        if (extension.Profile != Profile)
            return [];

        var data = extension.Data.Span;
        var elements = new List<RtpHeaderExtensionElement>();
        var offset = 0;
        while (offset < data.Length)
        {
            var header = data[offset];
            if (header == 0) // padding
            {
                offset++;
                continue;
            }

            var id = (byte)(header >> 4);
            if (id == 15) // reserved: stop parsing
                break;

            var length = (header & 0x0F) + 1;
            offset++;
            if (offset + length > data.Length) // truncated trailing element
                break;

            // Copy the value: extension.Data may alias a pooled/reused receive buffer, and the
            // returned element outlives this call. The copy is a few bytes (value ≤ 16).
            elements.Add(new RtpHeaderExtensionElement(id, extension.Data.Slice(offset, length).ToArray()));
            offset += length;
        }

        return elements;
    }
}
