using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;

namespace CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;

/// <summary>
/// Validates that a remote answer is a well-formed RFC 3264 §6 response to the local offer, so an
/// offerer never builds transport or tracks from a formally-parsed but mismatched answer (RFC 8829
/// setRemoteDescription). A hostile or buggy answerer must not be able to reorder m-lines, rename
/// MIDs, switch the transport profile, introduce an un-offered payload type, or claim a BUNDLE group
/// that was never offered — any of which would corrupt the shared media transport.
/// </summary>
internal static class SdpAnswerValidator
{
    /// <summary>
    /// Returns <see langword="null"/> when <paramref name="answer"/> is a valid response to
    /// <paramref name="offer"/>, or a human-readable reason describing the first violation found.
    /// </summary>
    public static string? Validate(SdpSessionDescription offer, SdpSessionDescription answer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(answer);

        var offered = offer.Media;
        var answered = answer.Media;

        // RFC 3264 §6: exactly one answer m-line per offered m-line, same order and media type. A declined
        // m-line is mirrored in place with port 0 — never dropped, added, or reordered.
        if (answered.Count != offered.Count)
            return $"m-line count {answered.Count} does not match the offer's {offered.Count}.";

        for (var i = 0; i < offered.Count; i++)
        {
            var o = offered[i];
            var a = answered[i];

            if (!a.MediaType.Equals(o.MediaType, StringComparison.OrdinalIgnoreCase))
                return $"m-line {i} media type '{a.MediaType}' does not match the offered '{o.MediaType}'.";

            // MID is preserved 1:1 (RFC 8829 §5.3.1) whenever the offer carried one.
            if (o.Mid is not null && !string.Equals(a.Mid, o.Mid, StringComparison.Ordinal))
                return $"m-line {i} mid '{a.Mid}' does not match the offered '{o.Mid}'.";

            // A declined m-line (port 0) needs no further checks; an accepted one must not expand the offer.
            if (a.Port <= 0)
                continue;

            // RFC 3264 §6: the transport profile in the answer must match the offer (e.g. an AVP offer
            // cannot be answered on SAVP), otherwise the keying/transport assumptions diverge.
            if (!a.Profile.Equals(o.Profile, StringComparison.OrdinalIgnoreCase))
                return $"m-line {i} profile '{a.Profile}' does not match the offered '{o.Profile}'.";

            // RFC 3264 §6.1: the answer selects payload types from those offered — it never introduces a
            // new format the offerer is not prepared to send or receive.
            var offeredPts = o.Codecs.Select(c => c.PayloadType).ToHashSet();
            foreach (var codec in a.Codecs)
            {
                if (!offeredPts.Contains(codec.PayloadType))
                    return $"m-line {i} answers payload type {codec.PayloadType} that was not offered.";
            }
        }

        // BUNDLE (RFC 9143 §7.3.3): when the offer asked for BUNDLE the answer must mirror it as a subset —
        // it may drop rejected mids but never add one that was not offered, and it may not silently omit the
        // group entirely (which would leave the offerer believing BUNDLE was agreed while the answer disagrees).
        var offerHasBundle = SdpBundleGroup.TryParse(offer.Group, out var offerMids) && offerMids.Count > 0;
        if (SdpBundleGroup.TryParse(answer.Group, out var answerMids) && answerMids.Count > 0)
        {
            var offeredMidSet = offerMids.ToHashSet(StringComparer.Ordinal);
            foreach (var mid in answerMids)
            {
                if (!offeredMidSet.Contains(mid))
                    return $"BUNDLE answer includes mid '{mid}' that was not in the offered group.";
            }
        }
        else if (offerHasBundle)
        {
            return "Offer required BUNDLE but the answer contains no BUNDLE group.";
        }

        return null;
    }
}
