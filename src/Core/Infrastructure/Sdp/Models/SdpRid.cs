namespace CalloraVoipSdk.Core.Infrastructure.Sdp.Models;

/// <summary>
/// One SDP <c>a=rid</c> attribute (RFC 8851 §10): an RTP stream identifier that names a single encoding
/// of a media section, e.g. <c>a=rid:hi send pt=96;max-width=1280</c>. Under BUNDLE simulcast (RFC 8853)
/// several <c>a=rid</c> lines share one m-line, each naming a distinct encoding carried on its own SSRC and
/// tagged per packet with the RID SDES header extension (RFC 8852). <see cref="Restrictions"/> is the
/// optional restriction list after the direction, preserved verbatim.
/// </summary>
internal sealed record SdpRid
{
    /// <summary>The RTP stream identifier (<c>rid-id</c>), e.g. <c>hi</c>, <c>mid</c>, <c>lo</c>.</summary>
    public required string Id { get; init; }

    /// <summary>The direction qualifier: <c>send</c> or <c>recv</c>.</summary>
    public required string Direction { get; init; }

    /// <summary>
    /// The restriction list after the direction (<c>pt=…;max-width=…;…</c>), preserved verbatim;
    /// <see langword="null"/> when absent.
    /// </summary>
    public string? Restrictions { get; init; }

    /// <summary>
    /// Parses the value after <c>a=rid:</c> (<c>&lt;rid-id&gt; &lt;direction&gt; [restrictions]</c>).
    /// Returns <see langword="null"/> on a missing id or a direction that is not <c>send</c>/<c>recv</c>.
    /// </summary>
    public static SdpRid? TryParse(string attrValue)
    {
        if (string.IsNullOrWhiteSpace(attrValue))
            return null;

        // Split into at most three parts: id, direction, and the (space-bearing) restriction list.
        var parts = attrValue.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return null;

        var direction = parts[1];
        if (!direction.Equals("send", StringComparison.Ordinal) &&
            !direction.Equals("recv", StringComparison.Ordinal))
            return null;

        return new SdpRid
        {
            Id = parts[0],
            Direction = direction,
            Restrictions = parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]) ? parts[2].Trim() : null,
        };
    }

    /// <summary>Serializes to the value string (without the leading <c>a=rid:</c>).</summary>
    public string Serialize() =>
        Restrictions is null ? $"{Id} {Direction}" : $"{Id} {Direction} {Restrictions}";
}
