namespace CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;

/// <summary>
/// Rejects contradictory occurrences of an SDP attribute that may appear at most once per level
/// (#160 P2-15). One instance per scope: the session level, and one per media section.
/// </summary>
/// <remarks>
/// The parser used to be last-wins for every singleton, which made the meaning of a description a
/// function of the order the peer happened to write its lines in. For the security-bearing attributes
/// that is not a cosmetic difference: a body carrying two <c>a=fingerprint</c> lines for the same hash
/// function, or <c>a=setup:passive</c> followed by <c>a=setup:active</c>, is read one way by a
/// last-wins parser and the other way by a first-wins one. Two endpoints then believe they agreed on
/// different things while looking at the same bytes — the classic parser-divergence shape, and the way
/// an attacker gets two peers to disagree about who authenticates whom.
///
/// The rule here is deliberately narrow: an exact repeat is accepted (implementations do emit
/// duplicates, and a duplicate says nothing new), a <em>contradiction</em> is a parse failure. Nothing
/// is silently preferred, because any preference would be this parser's opinion rather than the
/// peer's statement.
/// </remarks>
internal sealed class SdpSingletonGuard
{
    private readonly Dictionary<string, string> _seen = new(StringComparer.Ordinal);

    /// <summary>
    /// Records <paramref name="value"/> for <paramref name="attribute"/> and returns whether the
    /// caller should apply it — <see langword="false"/> for an exact repeat, which is a no-op.
    /// </summary>
    /// <exception cref="FormatException">
    /// The attribute was already present at this level with a different value.
    /// </exception>
    public bool Accept(string attribute, string value)
    {
        if (!_seen.TryGetValue(attribute, out var existing))
        {
            _seen[attribute] = value;
            return true;
        }

        if (!string.Equals(existing, value, StringComparison.Ordinal))
        {
            throw new FormatException(
                $"SDP declares contradictory '{attribute}' attributes at the same level (RFC 8866 §5.13).");
        }

        return false;
    }
}
