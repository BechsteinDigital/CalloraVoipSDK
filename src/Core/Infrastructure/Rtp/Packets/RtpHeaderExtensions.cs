namespace CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;

/// <summary>
/// Form-agnostic entry point for RFC 8285 header extensions (#224). Receiving, it dispatches on the
/// profile so both wire forms are read; sending, it picks the form the elements actually need — the
/// one-byte form while everything fits it, the two-byte form otherwise (RFC 8285 §4.3, final paragraph).
/// </summary>
/// <remarks>
/// Callers work in <see cref="RtpHeaderExtensionElement"/>s and let this choose, so no send path has to
/// know about wire forms. The one-byte form stays the default for everything that fits it: it is one byte
/// shorter per element, and it is what every peer understands. The two-byte form appears only when an
/// element requires it — an identifier above 14, a value longer than 16 bytes, or an empty value.
/// </remarks>
internal static class RtpHeaderExtensions
{
    /// <summary>
    /// Whether these elements cannot be expressed in the one-byte form and therefore require the
    /// two-byte one (RFC 8285 §4.2 limits: ids 1..14, values 1..16 bytes).
    /// </summary>
    public static bool RequiresTwoByteForm(IReadOnlyList<RtpHeaderExtensionElement> elements)
    {
        ArgumentNullException.ThrowIfNull(elements);
        foreach (var element in elements)
        {
            if (element.Id > OneByteRtpHeaderExtensions.MaxId
                || element.Value.Length is < 1 or > OneByteRtpHeaderExtensions.MaxValueLength)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Encodes the elements in the smallest form that can carry them. Returns <see langword="null"/> when
    /// there is nothing to encode. An identifier of 0 is rejected by whichever codec runs — it is padding
    /// in both forms, never an element.
    /// </summary>
    public static RtpExtension? Encode(IReadOnlyList<RtpHeaderExtensionElement> elements)
    {
        ArgumentNullException.ThrowIfNull(elements);
        return RequiresTwoByteForm(elements)
            ? TwoByteRtpHeaderExtensions.Encode(elements)
            : OneByteRtpHeaderExtensions.Encode(elements);
    }

    /// <summary>
    /// Parses an extension of either form into its elements, in wire order. An extension whose profile is
    /// neither form yields an empty list — an unknown profile is not something to guess at.
    /// </summary>
    public static IReadOnlyList<RtpHeaderExtensionElement> Parse(RtpExtension extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        if (extension.Profile == OneByteRtpHeaderExtensions.Profile)
            return OneByteRtpHeaderExtensions.Parse(extension);
        if (TwoByteRtpHeaderExtensions.IsProfile(extension.Profile))
            return TwoByteRtpHeaderExtensions.Parse(extension);

        return [];
    }

    /// <summary>
    /// Finds the value carried under <paramref name="id"/> in either form without allocating — the
    /// per-packet receive path (K3: no allocation on the media hot path). Returns
    /// <see langword="false"/> when the extension is absent, carries neither known profile, or has no
    /// element with that id. The returned span points into the extension's buffer and is only valid while
    /// it is.
    /// </summary>
    public static bool TryFindValue(RtpExtension? extension, byte id, out ReadOnlySpan<byte> value)
    {
        value = default;
        if (extension is null)
            return false;

        if (extension.Profile == OneByteRtpHeaderExtensions.Profile)
            return OneByteRtpHeaderExtensions.TryFindValue(extension, id, out value);
        if (TwoByteRtpHeaderExtensions.IsProfile(extension.Profile))
            return TwoByteRtpHeaderExtensions.TryFindValue(extension, id, out value);

        return false;
    }
}
