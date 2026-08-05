namespace CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;

/// <summary>
/// Semantic view of an <c>a=group</c> line for BUNDLE (RFC 5888 §5 / RFC 8843): the <c>BUNDLE</c>
/// semantics token followed by an ordered list of member MIDs. Modelling it — rather than matching the
/// raw string with <c>StartsWith("BUNDLE")</c> — stops a hostile offer from smuggling members: a
/// prefix match accepts <c>BUNDLEX …</c>, and treating "any active MID" as bundled lets an answer add
/// m-lines that were never in the offered group.
/// </summary>
internal static class SdpBundleGroup
{
    /// <summary>
    /// Parses a raw <c>a=group</c> value as a BUNDLE group. Returns <see langword="true"/> only when the
    /// first token is exactly <c>BUNDLE</c> (case-insensitive), yielding the ordered, <b>deduplicated</b>
    /// member MIDs (which may be empty for an empty group); otherwise the value is not a BUNDLE group.
    /// Deduplication enforces RFC 5888 §5 ("each MID-value MUST appear in the group value at most once")
    /// so a repeated member from a malformed offer cannot produce an invalid answer group.
    /// </summary>
    public static bool TryParse(string? rawGroup, out IReadOnlyList<string> mids)
    {
        mids = [];
        if (string.IsNullOrWhiteSpace(rawGroup))
            return false;

        var tokens = rawGroup.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0 || !tokens[0].Equals("BUNDLE", StringComparison.OrdinalIgnoreCase))
            return false;

        mids = tokens[1..].Distinct(StringComparer.Ordinal).ToArray();
        return true;
    }
}
