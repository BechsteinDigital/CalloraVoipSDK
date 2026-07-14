namespace CalloraVoipSdk.Core.Infrastructure.Rtp.Packetisation;

/// <summary>
/// Splits an H.264 Annex-B byte stream (ITU-T H.264 Annex B) into its NAL units:
/// start codes <c>00 00 01</c> and <c>00 00 00 01</c> delimit units; the returned
/// slices exclude the start codes and reference the input memory (no copies).
/// Limitation: trailing <c>cabac_zero_words</c> at a NAL end are indistinguishable
/// from the next start code's leading zero at this layer — at most one trailing zero
/// byte is attributed to the following 4-byte start code (libwebrtc behaviour).
/// </summary>
internal static class AnnexBParser
{
    /// <summary>Parses all NAL units of one access unit. Empty when no start code is found.</summary>
    public static IReadOnlyList<ReadOnlyMemory<byte>> ParseNalUnits(ReadOnlyMemory<byte> annexB)
    {
        var units = new List<ReadOnlyMemory<byte>>();
        var span = annexB.Span;
        var nalStart = -1;

        var i = 0;
        while (i + 2 < span.Length)
        {
            if (span[i] == 0 && span[i + 1] == 0 && span[i + 2] == 1)
            {
                if (nalStart >= 0)
                    AddUnit(units, annexB, nalStart, i);

                i += 3;
                nalStart = i;
                continue;
            }

            // A 4-byte start code is a zero followed by the 3-byte pattern — the scan
            // above lands on the 3-byte tail, so plain increment handles both forms.
            i++;
        }

        if (nalStart >= 0)
            AddUnit(units, annexB, nalStart, annexB.Length);

        return units;
    }

    private static void AddUnit(
        List<ReadOnlyMemory<byte>> units, ReadOnlyMemory<byte> annexB, int start, int end)
    {
        // Trim at most ONE trailing zero — the leading byte of the next start code's
        // 4-byte form. Trimming more would eat legitimate cabac_zero_words.
        var span = annexB.Span;
        if (end > start && end < annexB.Length && span[end - 1] == 0)
            end--;

        if (end > start)
            units.Add(annexB[start..end]);
    }
}
