namespace CalloraVoipSdk.Core.Infrastructure.Sdp.Models;

/// <summary>
/// One SDP <c>a=extmap</c> attribute (RFC 8285 §5): the mapping between a local identifier and an
/// RTP header-extension URI for a media section, e.g.
/// <c>a=extmap:3 http://www.ietf.org/id/draft-holmer-rmcat-transport-wide-cc-extensions-01</c>.
/// The optional direction qualifier (<c>/sendrecv</c> etc.) is preserved verbatim; extension
/// attributes after the URI are ignored. The identifier range that fits the one-byte header form is
/// 1..14 — enforced at negotiation, not here.
/// </summary>
internal sealed record SdpExtmap
{
    /// <summary>The extension identifier carried in the header-extension element.</summary>
    public required int Id { get; init; }

    /// <summary>Optional direction qualifier (<c>sendrecv</c>, <c>sendonly</c>, <c>recvonly</c>, <c>inactive</c>); null when absent.</summary>
    public string? Direction { get; init; }

    /// <summary>The header-extension URI.</summary>
    public required string Uri { get; init; }

    /// <summary>
    /// Parses the value after <c>a=extmap:</c> (<c>&lt;id&gt;["/"&lt;direction&gt;] &lt;uri&gt; [attrs]</c>).
    /// Returns <see langword="null"/> on a missing/non-numeric id or a missing URI.
    /// </summary>
    public static SdpExtmap? TryParse(string attrValue)
    {
        if (string.IsNullOrWhiteSpace(attrValue))
            return null;

        var parts = attrValue.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return null;

        var idPart = parts[0];
        string? direction = null;
        var slash = idPart.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0)
        {
            direction = idPart[(slash + 1)..];
            idPart = idPart[..slash];
        }

        if (!int.TryParse(idPart, out var id))
            return null;

        return new SdpExtmap
        {
            Id = id,
            Direction = string.IsNullOrWhiteSpace(direction) ? null : direction,
            Uri = parts[1],
        };
    }

    /// <summary>Serializes to the value string (without the leading <c>a=extmap:</c>).</summary>
    public string Serialize() =>
        Direction is null ? $"{Id} {Uri}" : $"{Id}/{Direction} {Uri}";
}
