using CalloraVoipSdk.Core.Infrastructure.Common.Protocols;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Reads the addresses a registrar says belong to this registration, from the 200 OK to REGISTER.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it answers.</b> "Which numbers can reach me on this line?" — the question every telephone
/// system has to answer and almost nothing else can. A trunk contract brings a list somebody can type
/// in; a registration account brings nothing, and its username is as often <c>admin123</c> as a number.
/// <c>P-Associated-URI</c> (RFC 3455 §5.1) is the standards-track answer: the registrar lists the URIs
/// associated with the address-of-record it just accepted.
/// </para>
/// <para>
/// <b>Not everyone sends it.</b> It comes from IMS and carrier registrars send it; a CPE box on the
/// local network generally does not. An empty result therefore means "nobody said", never "there are
/// none" — a caller has to treat the two the same way it treats a missing <c>Diversion</c> header.
/// </para>
/// <para>
/// <b>Order is preserved and duplicates are not.</b> RFC 3455 gives the first entry a meaning — it is
/// the default public identity, the one the network uses when a request does not say otherwise — so
/// sorting or de-duplicating into a set would throw away which number is the main one.
/// </para>
/// <para>
/// Both URI schemes occur: <c>sip:</c> from most registrars and <c>tel:</c> from some, often side by
/// side for the same number. They are returned as announced rather than reduced to digits: what is a
/// telephone number is a question about a dial plan, and this layer does not have one.
/// </para>
/// </remarks>
internal static class SipAssociatedUris
{
    /// <summary>
    /// Reads the associated addresses out of the header rows of a registration response.
    /// </summary>
    /// <param name="rows">All <c>P-Associated-URI</c> rows, as received.</param>
    /// <returns>
    /// The announced addresses, in the order the registrar gave them, without duplicates. Empty when
    /// the registrar said nothing — which is not the same as having none.
    /// </returns>
    public static IReadOnlyList<string> Parse(IReadOnlyList<string>? rows)
    {
        if (rows is null || rows.Count == 0)
        {
            return Array.Empty<string>();
        }

        var uris = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            // One row may carry several entries, and a display name may contain a comma inside quotes.
            // Splitting on every comma would cut "Meier, Dana" <sip:…> in half and produce two entries,
            // neither of them an address.
            foreach (var token in ProtocolCommonUtilities.SplitCommaSeparatedRespectingQuotes(row))
            {
                var uri = SipProtocol.ExtractUriFromNameAddr(token);
                if (string.IsNullOrWhiteSpace(uri))
                {
                    continue;
                }

                // No further trimming. ExtractUriFromNameAddr already separates the two cases the way
                // RFC 3261 §20 draws them: inside angle brackets a semicolon belongs to the URI
                // (sip:a@b;transport=tcp) and is kept, outside them it starts the header parameters and
                // is cut. Cutting again here would have removed a transport from an address that
                // needs it.
                if (seen.Add(uri))
                {
                    uris.Add(uri);
                }
            }
        }

        return uris.Count == 0 ? Array.Empty<string>() : uris;
    }
}
