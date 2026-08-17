namespace CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;

/// <summary>
/// Codec for the RFC 8285 §4.3 two-byte header-extension form, carried in an <see cref="RtpExtension"/>
/// whose profile is <c>0x100</c> in the top twelve bits (the low nibble holds the appbits, so <c>0x1000</c>
/// is the value with no appbits set). Each element is a two-byte header — one byte of identifier, one byte
/// of length — followed by that many value bytes; a zero identifier byte is inter-element padding.
/// </summary>
/// <remarks>
/// The form exists for what the one-byte form cannot express: identifiers above 14, values longer than
/// 16 bytes, and values of length zero. The Dependency Descriptor — the extension that carries key-frame
/// and layer information so a forwarder never has to read the payload — routinely exceeds 16 bytes, which
/// is why #224 is a prerequisite for #225.
/// <para>
/// Unlike the one-byte form there is no "stop parsing" identifier here: 15 is an ordinary id, and only 0
/// is reserved (padding). Parsing stays lenient on received data as RFC 8285 requires — padding is
/// skipped and a truncated trailing element is dropped rather than failing the packet.
/// </para>
/// </remarks>
internal static class TwoByteRtpHeaderExtensions
{
    /// <summary>The RFC 8285 §4.3 two-byte profile value with no appbits set.</summary>
    internal const ushort Profile = 0x1000;

    /// <summary>Mask selecting the profile's fixed twelve bits; the low nibble carries the appbits.</summary>
    internal const ushort ProfileMask = 0xFFF0;

    /// <summary>Lowest valid two-byte identifier (0 is reserved for padding).</summary>
    internal const byte MinId = 1;

    /// <summary>Highest valid two-byte identifier — the full byte is available.</summary>
    internal const byte MaxId = 255;

    /// <summary>Maximum value length representable by the 8-bit length field.</summary>
    internal const int MaxValueLength = 255;

    /// <summary>
    /// Whether <paramref name="profile"/> denotes the two-byte form, ignoring the appbits in the low
    /// nibble (RFC 8285 §4.3: the defined bits are <c>0x100</c>, the remaining four are application
    /// specific and carry no framing meaning).
    /// </summary>
    public static bool IsProfile(ushort profile) => (profile & ProfileMask) == Profile;

    /// <summary>
    /// Packs the elements into an <see cref="RtpExtension"/> (profile <c>0x1000</c>), zero-padded to a
    /// 32-bit boundary as the RTP extension body requires. Returns <see langword="null"/> when there is
    /// nothing to encode. Throws <see cref="ArgumentException"/> for an out-of-range identifier (1..255)
    /// or value length (0..255) — an invalid element is a construction bug, not silently dropped.
    /// </summary>
    public static RtpExtension? Encode(IReadOnlyList<RtpHeaderExtensionElement> elements)
    {
        ArgumentNullException.ThrowIfNull(elements);
        if (elements.Count == 0)
            return null;

        var unpadded = 0;
        foreach (var element in elements)
        {
            if (element.Id < MinId) // MaxId is byte.MaxValue: the type already bounds the top
                throw new ArgumentException(
                    $"Two-byte header-extension id {element.Id} is out of range (must be {MinId}..{MaxId}).",
                    nameof(elements));
            if (element.Value.Length > MaxValueLength)
                throw new ArgumentException(
                    $"Two-byte header-extension value length {element.Value.Length} is out of range "
                    + $"(must be 0..{MaxValueLength}).",
                    nameof(elements));
            unpadded += 2 + element.Value.Length;
        }

        var padded = (unpadded + 3) & ~3; // round up to a multiple of 4
        var data = new byte[padded];
        var offset = 0;
        foreach (var element in elements)
        {
            data[offset++] = element.Id;
            data[offset++] = (byte)element.Value.Length;
            element.Value.Span.CopyTo(data.AsSpan(offset));
            offset += element.Value.Length;
        }
        // The remaining bytes stay zero — RFC 8285 inter/trailing padding.

        return new RtpExtension { Profile = Profile, Data = data };
    }

    /// <summary>
    /// Parses a two-byte-form extension into its elements, in wire order, each value copied into an owned
    /// buffer (the source may alias a reused receive buffer). An extension of another profile yields an
    /// empty list.
    /// </summary>
    public static IReadOnlyList<RtpHeaderExtensionElement> Parse(RtpExtension extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        if (!IsProfile(extension.Profile))
            return [];

        var data = extension.Data.Span;
        var elements = new List<RtpHeaderExtensionElement>();
        var offset = 0;
        while (offset < data.Length)
        {
            var id = data[offset];
            if (id == 0) // padding
            {
                offset++;
                continue;
            }

            if (offset + 1 >= data.Length) // truncated header
                break;

            var length = data[offset + 1];
            offset += 2;
            if (offset + length > data.Length) // truncated trailing element
                break;

            // Copy the value: extension.Data may alias a pooled/reused receive buffer, and the returned
            // element outlives this call.
            elements.Add(new RtpHeaderExtensionElement(id, extension.Data.Slice(offset, length).ToArray()));
            offset += length;
        }

        return elements;
    }

    /// <summary>
    /// Finds the value carried under <paramref name="id"/> without allocating — the per-packet receive
    /// path. Returns <see langword="false"/> when no element with that id is present. The returned span
    /// points into <paramref name="extension"/>'s buffer and is only valid while it is.
    /// </summary>
    public static bool TryFindValue(RtpExtension extension, byte id, out ReadOnlySpan<byte> value)
    {
        value = default;
        if (extension is null || !IsProfile(extension.Profile))
            return false;

        var data = extension.Data.Span;
        var offset = 0;
        while (offset < data.Length)
        {
            var elementId = data[offset];
            if (elementId == 0) // padding
            {
                offset++;
                continue;
            }

            if (offset + 1 >= data.Length) // truncated header
                break;

            var length = data[offset + 1];
            offset += 2;
            if (offset + length > data.Length) // truncated trailing element
                break;

            if (elementId == id)
            {
                value = data.Slice(offset, length);
                return true;
            }

            offset += length;
        }

        return false;
    }
}
