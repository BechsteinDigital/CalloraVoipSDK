using CalloraVoipSdk.Core.Infrastructure.Common.Protocols;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Reads the retargeting history of an inbound INVITE from whichever header the carrier chose to
/// send it in, and returns it as one chronological list of the addresses a call was forwarded from.
/// </summary>
/// <remarks>
/// <para>
/// Two headers answer the same question and no carrier sends both consistently:
/// <c>Diversion</c> (RFC 5806, informational but widely deployed) and <c>History-Info</c>
/// (RFC 4244, the standards-track successor). A consumer that reads only one is correct with some
/// carriers and silently blind with the others — and "silently blind" is the dangerous half, because
/// a forwarded call then looks exactly like a direct one.
/// </para>
/// <para>
/// <b>They are ordered opposite ways, which is the trap.</b> Diversion is most-recent-first: the
/// entry at the top is the party that forwarded the call last. History-Info is oldest-first, ordered
/// by its <c>index</c> parameter, and its entries are <em>targets</em> rather than forwarders — the
/// last one is where the request currently is, which is us. Both are normalised here to the same
/// thing: who forwarded this call, oldest first, excluding ourselves.
/// </para>
/// <para>
/// History-Info wins when both are present. It carries the full chain with an explicit order, where
/// Diversion carries a flat list whose order is a convention; when the two disagree, the one that
/// states its order is the one to believe.
/// </para>
/// </remarks>
internal static class SipDiversionHistory
{
    /// <summary>
    /// Builds the chronological forwarding chain from the raw header rows of an inbound INVITE.
    /// </summary>
    /// <param name="historyInfoRows">All <c>History-Info</c> header rows, as received.</param>
    /// <param name="diversionRows">All <c>Diversion</c> header rows, as received.</param>
    /// <param name="currentTargetUri">
    /// The URI this request is currently addressed to (the Request-URI). History-Info lists it as its
    /// final entry; without it that entry would be reported as a party that forwarded the call, which
    /// is us. Compared per RFC 3261 §19.1.4, not as a string — <c>sip:a@Example.COM</c> and
    /// <c>sip:a@example.com</c> are the same address, <c>sip:a@x</c> and <c>sip:a@x:5060</c> are not.
    /// </param>
    /// <returns>
    /// The addresses the call was forwarded from, oldest first. Empty when no retargeting was
    /// reported — which is not the same as "the call was not forwarded", only that nothing said so.
    /// </returns>
    public static IReadOnlyList<string> Parse(
        IReadOnlyList<string>? historyInfoRows,
        IReadOnlyList<string>? diversionRows,
        string? currentTargetUri)
    {
        var fromHistoryInfo = ParseHistoryInfo(historyInfoRows, currentTargetUri);
        if (fromHistoryInfo.Count > 0)
            return fromHistoryInfo;

        return ParseDiversion(diversionRows);
    }

    /// <summary>
    /// Parses <c>History-Info</c> (RFC 4244) into oldest-first order, dropping the entry that names
    /// where the request already is.
    /// </summary>
    private static IReadOnlyList<string> ParseHistoryInfo(
        IReadOnlyList<string>? rows,
        string? currentTargetUri)
    {
        if (rows is null || rows.Count == 0)
            return Array.Empty<string>();

        var entries = new List<HistoryInfoEntry>();
        foreach (var row in rows)
        {
            foreach (var token in ProtocolCommonUtilities.SplitCommaSeparatedRespectingQuotes(row))
            {
                var uri = SipProtocol.ExtractUriFromNameAddr(token);
                if (string.IsNullOrWhiteSpace(uri))
                    continue;

                entries.Add(new HistoryInfoEntry(uri, ReadIndex(token)));
            }
        }

        if (entries.Count == 0)
            return Array.Empty<string>();

        // The index is a dotted path ("1", "1.1", "1.2.1") and its segments are numbers, so it sorts
        // segment by segment. Comparing the strings instead puts "1.10" before "1.2", which reverses
        // the two most recent hops of any call forwarded more than nine times along one branch.
        entries.Sort(static (left, right) => CompareIndexes(left.Index, right.Index));

        var chain = new List<string>(entries.Count);
        foreach (var entry in entries)
        {
            // Everything except where we are now. Carriers differ on whether they append the final
            // hop at all, so this drops it wherever it appears rather than assuming it is last.
            if (!string.IsNullOrWhiteSpace(currentTargetUri)
                && SipUriProtocol.SipUriEqual(entry.Uri, currentTargetUri))
            {
                continue;
            }

            chain.Add(entry.Uri);
        }

        return chain;
    }

    /// <summary>
    /// Parses <c>Diversion</c> (RFC 5806) into oldest-first order — the reverse of how it arrives.
    /// </summary>
    private static IReadOnlyList<string> ParseDiversion(IReadOnlyList<string>? rows)
    {
        if (rows is null || rows.Count == 0)
            return Array.Empty<string>();

        var mostRecentFirst = new List<string>();
        foreach (var row in rows)
        {
            foreach (var token in ProtocolCommonUtilities.SplitCommaSeparatedRespectingQuotes(row))
            {
                var uri = SipProtocol.ExtractUriFromNameAddr(token);
                if (string.IsNullOrWhiteSpace(uri))
                    uri = token.Trim().Trim('"');

                if (!string.IsNullOrWhiteSpace(uri))
                    mostRecentFirst.Add(uri);
            }
        }

        mostRecentFirst.Reverse();
        return mostRecentFirst;
    }

    /// <summary>
    /// Reads the <c>index</c> header parameter of one History-Info entry, or an empty string when it
    /// carries none. An entry without an index keeps its arrival order relative to the others.
    /// </summary>
    private static string ReadIndex(string entry)
    {
        var closingAngle = entry.LastIndexOf('>');
        var parameterStart = closingAngle >= 0 ? closingAngle + 1 : 0;

        foreach (var parameter in entry[parameterStart..].Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = parameter.IndexOf('=');
            if (separator <= 0)
                continue;

            if (parameter[..separator].Trim().Equals("index", StringComparison.OrdinalIgnoreCase))
                return parameter[(separator + 1)..].Trim();
        }

        return string.Empty;
    }

    /// <summary>Compares two dotted History-Info indexes segment by segment, numerically.</summary>
    private static int CompareIndexes(string left, string right)
    {
        var leftSegments = left.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var rightSegments = right.Split('.', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < Math.Min(leftSegments.Length, rightSegments.Length); i++)
        {
            var leftIsNumber = int.TryParse(leftSegments[i], out var leftValue);
            var rightIsNumber = int.TryParse(rightSegments[i], out var rightValue);

            // A non-numeric segment is not something RFC 4244 allows, but a malformed header must not
            // decide the order by accident: fall back to an ordinal comparison of that segment.
            if (!leftIsNumber || !rightIsNumber)
            {
                var textual = string.CompareOrdinal(leftSegments[i], rightSegments[i]);
                if (textual != 0)
                    return textual;

                continue;
            }

            if (leftValue != rightValue)
                return leftValue.CompareTo(rightValue);
        }

        return leftSegments.Length.CompareTo(rightSegments.Length);
    }
}
