using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;

namespace CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;

/// <summary>
/// Negotiates the RTP header-extension mappings of a media section (<c>a=extmap</c>, RFC 8285 §5).
/// </summary>
/// <remarks>
/// Extracted from <see cref="SdpOfferAnswerNegotiator"/>, which had reached the 1000-line limit. The
/// extmap rules form one self-contained question — which id means which URI, in which direction — and
/// they are the same for audio and video, offer and answer.
/// </remarks>
internal static class SdpExtmapNegotiation
{
    // RFC 8285 §4.2: the one-byte header form uses ids 1..14 (0 is padding, 15 is reserved).
    private const int OneByteMaxExtensionId = 14;

    /// <summary>
    /// Offer: assigns sequential one-byte ids to the supported extension URIs (RFC 8285 §5).
    /// </summary>
    /// <remarks>
    /// Only the first 14 fit the one-byte form; any beyond that are dropped (the SDK's supported set
    /// is small).
    /// </remarks>
    public static IReadOnlyList<SdpExtmap> BuildOffer(IReadOnlyList<string> uris)
    {
        if (uris.Count == 0)
            return [];

        var extmaps = new List<SdpExtmap>(Math.Min(uris.Count, OneByteMaxExtensionId));
        for (var i = 0; i < uris.Count && i < OneByteMaxExtensionId; i++)
            extmaps.Add(new SdpExtmap { Id = i + 1, Uri = uris[i] });
        return extmaps;
    }

    /// <summary>
    /// The MID SDES header extension (RFC 9143 / RFC 8843 §9) rides every bundled m-line so the peer
    /// stamps each packet's MID on the shared transport, under the SAME id on every m-line.
    /// </summary>
    /// <remarks>
    /// Offered first so <see cref="BuildOffer"/> assigns it id 1. Outside BUNDLE the extmaps are
    /// unchanged.
    /// </remarks>
    public static IReadOnlyList<string> WithBundledMid(bool bundle, IReadOnlyList<string> uris) =>
        bundle ? [RtpHeaderExtensionUris.Mid, .. uris] : uris;

    /// <summary>
    /// Adds the MID URI to a supported set so the answer echoes the offered MID extension (RFC 9143).
    /// </summary>
    /// <remarks>
    /// A no-op when the offer carried no MID extension (outside BUNDLE), so non-bundle answers are
    /// unchanged.
    /// </remarks>
    public static IReadOnlyList<string> WithMid(IReadOnlyList<string> supportedUris) =>
        [RtpHeaderExtensionUris.Mid, .. supportedUris];

    /// <summary>
    /// Answer: for each offered extmap whose URI we support, echoes it under the offered id
    /// (RFC 8285 §5 — the offerer owns the id assignment); unsupported extensions are dropped.
    /// </summary>
    /// <remarks>
    /// #160 P2-13: the mapping has to stay a bijection. An offer naming the same id for two URIs — or
    /// the same URI under two ids — leaves the demultiplexer no way to tell what an id means, and
    /// echoing both would confirm an ambiguity as if it were a negotiation. The first mapping wins;
    /// any later one colliding on either side is dropped.
    ///
    /// Only one-byte ids are echoed, since that is the form the SDK reads and writes.
    /// </remarks>
    public static IReadOnlyList<SdpExtmap> BuildAnswer(
        IReadOnlyList<SdpExtmap> offered,
        IReadOnlyList<string> supportedUris)
    {
        if (offered.Count == 0 || supportedUris.Count == 0)
            return [];

        var extmaps = new List<SdpExtmap>();
        var takenIds = new HashSet<int>();
        var takenUris = new HashSet<string>(StringComparer.Ordinal);

        foreach (var extmap in offered)
        {
            if (extmap.Id is < 1 or > OneByteMaxExtensionId)
                continue;
            if (!supportedUris.Contains(extmap.Uri, StringComparer.Ordinal))
                continue;
            if (!takenIds.Add(extmap.Id) || !takenUris.Add(extmap.Uri))
                continue;

            extmaps.Add(new SdpExtmap
            {
                Id = extmap.Id,
                Uri = extmap.Uri,
                // The direction is part of the negotiation, not decoration (RFC 8285 §5): an extension
                // the peer will only send needs an answer saying we will only receive. Dropping it, as
                // this did before, silently promoted every extension to sendrecv.
                Direction = MirrorDirection(extmap.Direction),
            });
        }

        return extmaps;
    }

    /// <summary>
    /// Mirrors an <c>a=extmap</c> direction qualifier for the answer (RFC 8285 §5).
    /// </summary>
    internal static string? MirrorDirection(string? offeredDirection)
    {
        if (string.IsNullOrWhiteSpace(offeredDirection))
            return null;   // absent means sendrecv; stay silent rather than adding a qualifier

        return offeredDirection.Trim().ToLowerInvariant() switch
        {
            "sendonly" => "recvonly",
            "recvonly" => "sendonly",
            "sendrecv" => "sendrecv",
            "inactive" => "inactive",
            // An unknown qualifier is not a direction we can answer. Leaving it off falls back to the
            // sendrecv default rather than echoing a token neither side can act on.
            _ => null,
        };
    }
}
