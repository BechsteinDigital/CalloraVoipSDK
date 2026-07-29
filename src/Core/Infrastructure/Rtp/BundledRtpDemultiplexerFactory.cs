namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Builds a <see cref="BundledRtpDemultiplexer"/> from the negotiated BUNDLE m-lines (their MID and
/// payload types). The routing brain (B2a) takes a ready payload-type→MID map, but that map may only
/// contain payload types that belong to exactly one m-line: a payload type shared across m-lines cannot
/// disambiguate on its own and must be resolved by MID or SSRC instead (RFC 8843 §9.2). This factory
/// applies that rule — it maps each unambiguous payload type to its MID and drops the shared ones — so
/// callers configuring the transport (ADR-010 B2c/B4) don't reimplement it.
/// </summary>
internal static class BundledRtpDemultiplexerFactory
{
    /// <summary>
    /// Creates the demultiplexer for the given m-lines.
    /// </summary>
    /// <param name="midExtensionId">
    /// The negotiated MID header-extension id (<c>0</c> when MID was not negotiated — the demultiplexer
    /// then relies on SSRC and unambiguous payload types only).
    /// </param>
    /// <param name="payloadTypesByMid">Each m-line's MID mapped to the payload types it negotiated.</param>
    /// <param name="ridExtensionId">
    /// The negotiated RID header-extension id (RFC 8852 <c>sdes:rtp-stream-id</c>), or
    /// <see langword="null"/> when no simulcast encoding was negotiated. Passed through to the
    /// demultiplexer for recv-side simulcast RID resolution (RFC 8853); it does not affect m-line demux.
    /// </param>
    /// <exception cref="ArgumentException">A MID is empty.</exception>
    public static BundledRtpDemultiplexer Create(
        byte midExtensionId,
        IReadOnlyDictionary<string, IReadOnlyCollection<int>> payloadTypesByMid,
        byte? ridExtensionId = null)
    {
        ArgumentNullException.ThrowIfNull(payloadTypesByMid);

        // Count how many m-lines use each payload type; a type used by exactly one m-line can demultiplex.
        var midsByPayloadType = new Dictionary<int, List<string>>();
        foreach (var (mid, payloadTypes) in payloadTypesByMid)
        {
            ArgumentException.ThrowIfNullOrEmpty(mid);
            if (payloadTypes is null)
                continue;

            foreach (var payloadType in payloadTypes)
            {
                if (!midsByPayloadType.TryGetValue(payloadType, out var mids))
                    midsByPayloadType[payloadType] = mids = [];
                if (!mids.Contains(mid))
                    mids.Add(mid);
            }
        }

        var payloadTypeToMid = new Dictionary<int, string>();
        foreach (var (payloadType, mids) in midsByPayloadType)
        {
            if (mids.Count == 1) // unambiguous: this payload type belongs to a single m-line
                payloadTypeToMid[payloadType] = mids[0];
        }

        return new BundledRtpDemultiplexer(
            midExtensionId,
            new HashSet<string>(payloadTypesByMid.Keys, StringComparer.Ordinal),
            payloadTypeToMid,
            ridExtensionId);
    }
}
