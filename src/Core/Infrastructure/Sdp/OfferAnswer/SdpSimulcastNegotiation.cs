using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;

namespace CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;

/// <summary>
/// Negotiates the simulcast attributes of a video media section (<c>a=rid</c> / <c>a=simulcast</c>,
/// RFC 8853) for both the offer and the answer.
/// </summary>
/// <remarks>
/// Extracted from <see cref="SdpOfferAnswerNegotiator"/>, which had reached the 1000-line limit (#369). The
/// simulcast rules are one self-contained question — which layers, in which direction, restricted to which
/// codec — shared by the offer and answer paths, mirroring the split-out <see cref="SdpExtmapNegotiation"/>.
/// </remarks>
internal static class SdpSimulcastNegotiation
{
    /// <summary>
    /// Builds the <c>a=rid</c> / <c>a=simulcast</c> lines for a video OFFER (RFC 8853): one <c>a=rid</c> per
    /// layer restricted to the primary (first, non-RTX) video codec's payload type, plus one
    /// <c>a=simulcast</c> listing the layer ids in order.
    /// </summary>
    /// <remarks>
    /// Send rids (this side simulcasts) and recv rids (this side asks the peer to simulcast, RFC 8853 §5.3)
    /// are independent: either, both, or neither. A receive-only offerer — the conference host — must declare
    /// recv rids or the peer sends a single stream (#317). A direction with fewer than two distinct ids is not
    /// simulcast and is dropped here at the SDP origin (#369); returns <c>([], null)</c> when nothing
    /// survives, leaving a plain single-stream m-line.
    /// </remarks>
    public static (IReadOnlyList<SdpRid> Rids, SdpSimulcast? Simulcast) BuildOffer(
        IReadOnlyList<string> sendRids, IReadOnlyList<string> recvRids, IReadOnlyList<SdpCodecDefinition> videoCodecs)
    {
        var send = DistinctLayers(sendRids);
        var recv = DistinctLayers(recvRids);
        if (send.Count == 0 && recv.Count == 0)
            return ([], null);

        var primaryPt = videoCodecs[0].PayloadType;
        var rids = new List<SdpRid>(send.Count + recv.Count);
        rids.AddRange(send.Select(rid => new SdpRid { Id = rid, Direction = "send", Restrictions = $"pt={primaryPt}" }));
        rids.AddRange(recv.Select(rid => new SdpRid { Id = rid, Direction = "recv", Restrictions = $"pt={primaryPt}" }));
        return (rids, new SdpSimulcast { Send = send, Recv = recv });
    }

    /// <summary>
    /// Builds the <c>a=rid</c> / <c>a=simulcast</c> lines for a video ANSWER (RFC 8853 §5.3, RFC 8829 §5.3.1
    /// and the W3C answerer rules) by mirroring the offered simulcast.
    /// </summary>
    /// <remarks>
    /// An offered <c>a=simulcast:send</c> (the peer will simulcast) is confirmed with <c>a=simulcast:recv</c>
    /// so this side receives those layers (case A — the common SFU topology). An offered
    /// <c>a=simulcast:recv</c> (the peer asks us to simulcast) is answered with <c>a=simulcast:send</c> for the
    /// layers we are configured to produce (case B) — the ids come from the offer, in the offer's order (W3C:
    /// the sendEncodings are created from the simulcast attribute's rid values), intersected with what the
    /// local options actually offer to send. Only the intersection is confirmed (RFC 8853 §5.1). The
    /// confirmation is useless unless the offer carried the RID header extension (RFC 8852) — without a
    /// per-packet label the peer cannot demux the layers — so a simulcast offer that omitted it yields a plain
    /// single-stream answer.
    /// </remarks>
    public static (IReadOnlyList<SdpRid> Rids, SdpSimulcast? Simulcast) BuildAnswer(
        SdpMediaDescription offered,
        IReadOnlyList<string> localSendRids,
        IReadOnlyList<SdpCodecDefinition> negotiatedCodecs)
    {
        if (offered.Simulcast is not { } offeredSc)
            return ([], null);

        // RFC 8852: no RID extension in the offer means the layers cannot be labelled per packet, so a
        // confirmation would be worthless — decline simulcast and answer a single stream (#369 criterion 2).
        if (!offered.Extensions.Any(e => string.Equals(e.Uri, RtpHeaderExtensionUris.Rid, StringComparison.Ordinal)))
            return ([], null);

        // Case A: we receive every layer the offer declared it will send (accepted in the offer's order).
        var answerRecv = offeredSc.Send;
        // Case B: we send only the layers the offer asked for AND the local options are configured to produce.
        var localSend = new HashSet<string>(localSendRids, StringComparer.Ordinal);
        var answerSend = offeredSc.Recv.Where(localSend.Contains).ToArray();

        // Reuse the offer builder so the answer's a=rid / a=simulcast shape — and the <2-distinct drop — are
        // identical to the offer side.
        return BuildOffer(answerSend, answerRecv, negotiatedCodecs);
    }

    // A simulcast direction needs at least two distinct layer ids: a single a=rid is not simulcast, and Chrome
    // strips a lone rid and never enters simulcast (#369), so a one-layer direction is dropped rather than
    // announced. Order and first-occurrence are preserved.
    private static IReadOnlyList<string> DistinctLayers(IEnumerable<string> ids)
    {
        var distinct = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
            if (!string.IsNullOrEmpty(id) && seen.Add(id))
                distinct.Add(id);
        return distinct.Count >= 2 ? distinct : [];
    }
}
