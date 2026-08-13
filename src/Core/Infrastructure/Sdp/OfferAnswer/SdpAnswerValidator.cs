using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;

namespace CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;

/// <summary>
/// Validates that a remote answer is a well-formed RFC 3264 §6 response to the local offer, so an
/// offerer never builds transport or tracks from a formally-parsed but mismatched answer (RFC 8829
/// setRemoteDescription). A hostile or buggy answerer must not be able to reorder m-lines, rename
/// MIDs, switch the transport profile, introduce an un-offered payload type, or claim a BUNDLE group
/// that was never offered — any of which would corrupt the shared media transport.
/// <para>
/// #160 P1-2b extends this from the answer's <em>structure</em> to the <em>attribute values</em> inside
/// it. Validating the shape while letting the contents through left an answer free to enable
/// <c>rtcp-mux</c>, flip the media direction, take a DTLS setup role the offer did not allow, or add
/// feedback, header extensions and format parameters that were never on the table — each of which the
/// offerer would then act on as though it had agreed to them.
/// </para>
/// </summary>
internal static class SdpAnswerValidator
{
    /// <summary>
    /// Returns <see langword="null"/> when <paramref name="answer"/> is a valid response to
    /// <paramref name="offer"/>, or the first violation found.
    /// </summary>
    public static SdpAnswerValidationError? Validate(SdpSessionDescription offer, SdpSessionDescription answer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(answer);

        var offered = offer.Media;
        var answered = answer.Media;

        // RFC 3264 §6: exactly one answer m-line per offered m-line, same order and media type. A declined
        // m-line is mirrored in place with port 0 — never dropped, added, or reordered.
        if (answered.Count != offered.Count)
        {
            return new SdpAnswerValidationError(
                SdpAnswerViolation.MediaSectionCount, null,
                $"m-line count {answered.Count} does not match the offer's {offered.Count}.");
        }

        for (var i = 0; i < offered.Count; i++)
        {
            if (ValidateMediaSection(i, offered[i], answered[i]) is { } error)
                return error;
        }

        return ValidateBundle(offer, answer);
    }

    private static SdpAnswerValidationError? ValidateMediaSection(int index, SdpMediaDescription o, SdpMediaDescription a)
    {
        if (!a.MediaType.Equals(o.MediaType, StringComparison.OrdinalIgnoreCase))
        {
            return new SdpAnswerValidationError(
                SdpAnswerViolation.MediaType, index,
                $"m-line {index} media type '{a.MediaType}' does not match the offered '{o.MediaType}'.");
        }

        // MID is preserved 1:1 (RFC 8829 §5.3.1) whenever the offer carried one.
        if (o.Mid is not null && !string.Equals(a.Mid, o.Mid, StringComparison.Ordinal))
        {
            return new SdpAnswerValidationError(
                SdpAnswerViolation.Mid, index,
                $"m-line {index} mid '{a.Mid}' does not match the offered '{o.Mid}'.");
        }

        // A declined m-line (port 0) needs no further checks; an accepted one must not expand the offer.
        if (a.Port <= 0)
            return null;

        // RFC 3264 §6: the transport profile in the answer must match the offer (e.g. an AVP offer
        // cannot be answered on SAVP), otherwise the keying/transport assumptions diverge.
        if (!a.Profile.Equals(o.Profile, StringComparison.OrdinalIgnoreCase))
        {
            return new SdpAnswerValidationError(
                SdpAnswerViolation.Profile, index,
                $"m-line {index} profile '{a.Profile}' does not match the offered '{o.Profile}'.");
        }

        // RFC 3264 §6.1: the answer selects payload types from those offered — it never introduces a
        // new format the offerer is not prepared to send or receive.
        var offeredPts = o.Codecs.Select(c => c.PayloadType).ToHashSet();
        foreach (var codec in a.Codecs)
        {
            if (!offeredPts.Contains(codec.PayloadType))
            {
                return new SdpAnswerValidationError(
                    SdpAnswerViolation.UnofferedPayloadType, index,
                    $"m-line {index} answers payload type {codec.PayloadType} that was not offered.");
            }
        }

        return ValidateDirection(index, o, a)
            ?? ValidateRtcpMux(index, o, a)
            ?? ValidateDtlsSetup(index, o, a)
            ?? ValidateFeedback(index, o, a, offeredPts)
            ?? ValidateExtensions(index, o, a)
            ?? ValidateFormatParameters(index, o, a, offeredPts);
    }

    // RFC 3264 §6.1: the answer's direction is determined by the offer's. An offer of sendonly can only be
    // answered recvonly or inactive, and so on — an answer may narrow the stream but never widen it. Letting
    // it widen meant a peer could answer sendrecv to our recvonly offer and start sending media we never
    // agreed to receive, on a track the offerer built as receive-only.
    private static SdpAnswerValidationError? ValidateDirection(int index, SdpMediaDescription o, SdpMediaDescription a)
    {
        var allowed = o.Direction switch
        {
            SdpMediaDirection.SendRecv => new[] { SdpMediaDirection.SendRecv, SdpMediaDirection.SendOnly, SdpMediaDirection.RecvOnly, SdpMediaDirection.Inactive },
            SdpMediaDirection.SendOnly => [SdpMediaDirection.RecvOnly, SdpMediaDirection.Inactive],
            SdpMediaDirection.RecvOnly => [SdpMediaDirection.SendOnly, SdpMediaDirection.Inactive],
            _ => [SdpMediaDirection.Inactive],
        };

        if (Array.IndexOf(allowed, a.Direction) >= 0)
            return null;

        return new SdpAnswerValidationError(
            SdpAnswerViolation.Direction, index,
            $"m-line {index} answers direction '{a.Direction}', which is not a valid response to the offered '{o.Direction}' (RFC 3264 §6.1).");
    }

    // RFC 5761 §5.1.1: rtcp-mux is negotiated — the answerer may accept it or not, but it cannot introduce
    // it. An offerer that did not offer mux has a separate RTCP port open and expects RTCP there; an answer
    // that turns mux on unilaterally would send RTCP to a port nothing is reading.
    private static SdpAnswerValidationError? ValidateRtcpMux(int index, SdpMediaDescription o, SdpMediaDescription a)
        => a.RtcpMux && !o.RtcpMux
            ? new SdpAnswerValidationError(
                SdpAnswerViolation.RtcpMuxNotOffered, index,
                $"m-line {index} answers with rtcp-mux, which was not offered (RFC 5761 §5.1.1).")
            : null;

    // RFC 5763 §5: the offerer sends setup:actpass (or a concrete role); the answerer must pick a concrete
    // one, and the opposite of a concrete offer. An answer of actpass leaves both sides waiting for the
    // other to start the handshake; an answer that repeats our own role makes both sides clients or both
    // servers, and the DTLS handshake never completes.
    private static SdpAnswerValidationError? ValidateDtlsSetup(int index, SdpMediaDescription o, SdpMediaDescription a)
    {
        if (o.DtlsSetup is null || a.DtlsSetup is null)
            return null;

        var offered = o.DtlsSetup.Trim();
        var answered = a.DtlsSetup.Trim();

        var valid = offered.Equals("actpass", StringComparison.OrdinalIgnoreCase)
            ? answered.Equals("active", StringComparison.OrdinalIgnoreCase) || answered.Equals("passive", StringComparison.OrdinalIgnoreCase)
            : offered.Equals("active", StringComparison.OrdinalIgnoreCase)
                ? answered.Equals("passive", StringComparison.OrdinalIgnoreCase)
                : offered.Equals("passive", StringComparison.OrdinalIgnoreCase)
                    ? answered.Equals("active", StringComparison.OrdinalIgnoreCase)
                    : true;   // an offer we did not generate in a known role: leave the role check to the DTLS layer

        return valid
            ? null
            : new SdpAnswerValidationError(
                SdpAnswerViolation.DtlsSetupRole, index,
                $"m-line {index} answers setup:'{answered}', which is not a valid response to the offered setup:'{offered}' (RFC 5763 §5).");
    }

    // RFC 4585 §4: feedback is negotiated per payload type. An answer may keep or drop what was offered but
    // not add to it — an unrequested NACK or PLI would make the offerer honour retransmission or key-frame
    // requests it never advertised support for.
    private static SdpAnswerValidationError? ValidateFeedback(
        int index, SdpMediaDescription o, SdpMediaDescription a, HashSet<int> offeredPts)
    {
        if (a.RtcpFeedback.Count == 0)
            return null;

        var offeredFeedback = o.RtcpFeedback
            .Select(f => FeedbackKey(f))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A wildcard offer ("*") covers every payload type for that feedback type.
        var offeredWildcards = o.RtcpFeedback
            .Where(f => f.PayloadType == "*")
            .Select(f => FeedbackTypeKey(f))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var feedback in a.RtcpFeedback)
        {
            if (offeredFeedback.Contains(FeedbackKey(feedback)) || offeredWildcards.Contains(FeedbackTypeKey(feedback)))
                continue;

            return new SdpAnswerValidationError(
                SdpAnswerViolation.UnofferedRtcpFeedback, index,
                $"m-line {index} answers rtcp-fb '{FeedbackKey(feedback)}' that was not offered (RFC 4585 §4).");
        }

        // An answer must not attach feedback to a payload type it did not even answer with.
        foreach (var feedback in a.RtcpFeedback)
        {
            if (feedback.PayloadType != "*"
                && int.TryParse(feedback.PayloadType, out var pt)
                && !offeredPts.Contains(pt))
            {
                return new SdpAnswerValidationError(
                    SdpAnswerViolation.UnofferedRtcpFeedback, index,
                    $"m-line {index} answers rtcp-fb for payload type {pt} that was not offered.");
            }
        }

        return null;
    }

    private static string FeedbackKey(SdpRtcpFeedback f)
        => $"{f.PayloadType} {f.FeedbackType}{(f.Parameter is null ? string.Empty : " " + f.Parameter)}";

    private static string FeedbackTypeKey(SdpRtcpFeedback f)
        => $"{f.FeedbackType}{(f.Parameter is null ? string.Empty : " " + f.Parameter)}";

    // RFC 8285 §5: the answerer picks from the offered extensions and must keep the offerer's id mapping.
    // An answer that maps a different URI to an offered id — or introduces an id of its own — would make the
    // offerer read one extension's bytes as another's on every packet.
    private static SdpAnswerValidationError? ValidateExtensions(int index, SdpMediaDescription o, SdpMediaDescription a)
    {
        if (a.Extensions.Count == 0)
            return null;

        var offeredById = o.Extensions.ToDictionary(e => e.Id, e => e.Uri);
        foreach (var extension in a.Extensions)
        {
            if (!offeredById.TryGetValue(extension.Id, out var offeredUri))
            {
                return new SdpAnswerValidationError(
                    SdpAnswerViolation.UnofferedHeaderExtension, index,
                    $"m-line {index} answers extmap id {extension.Id} ('{extension.Uri}') that was not offered (RFC 8285 §5).");
            }

            if (!string.Equals(offeredUri, extension.Uri, StringComparison.OrdinalIgnoreCase))
            {
                return new SdpAnswerValidationError(
                    SdpAnswerViolation.UnofferedHeaderExtension, index,
                    $"m-line {index} answers extmap id {extension.Id} as '{extension.Uri}' but it was offered as '{offeredUri}'.");
            }
        }

        return null;
    }

    // RFC 3264 §6.1: format parameters describe a payload type that was offered. An answer that attaches
    // fmtp to a type the offer did not carry is describing a format the offerer never advertised. The RTX
    // association (RFC 4588 §8.1) gets the same treatment: apt must point at an offered payload type, or
    // the retransmission stream refers to a primary stream that does not exist.
    private static SdpAnswerValidationError? ValidateFormatParameters(
        int index, SdpMediaDescription o, SdpMediaDescription a, HashSet<int> offeredPts)
    {
        if (a.Fmtp.Count == 0)
            return null;

        var offeredFmtpPts = o.Fmtp.Select(f => f.PayloadType).ToHashSet();
        foreach (var fmtp in a.Fmtp)
        {
            if (!offeredPts.Contains(fmtp.PayloadType))
            {
                return new SdpAnswerValidationError(
                    SdpAnswerViolation.UnofferedFormatParameters, index,
                    $"m-line {index} answers fmtp for payload type {fmtp.PayloadType} that was not offered.");
            }

            if (TryReadRtxApt(fmtp.Parameters) is { } apt && !offeredPts.Contains(apt))
            {
                return new SdpAnswerValidationError(
                    SdpAnswerViolation.RtxAssociatedPayloadTypeNotOffered, index,
                    $"m-line {index} answers RTX payload type {fmtp.PayloadType} with apt={apt}, which was not offered (RFC 4588 §8.1).");
            }

            _ = offeredFmtpPts;   // the offer's fmtp set is not required to match; only the payload type is
        }

        return null;
    }

    // Reads the RTX associated payload type from an fmtp parameter list ("apt=96"), or null when absent.
    // Matched as an exact key so "xapt=96" or a value inside another parameter is not mistaken for it.
    private static int? TryReadRtxApt(string parameters)
    {
        foreach (var part in parameters.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0)
                continue;

            if (!part.AsSpan(0, separator).Trim().Equals("apt", StringComparison.OrdinalIgnoreCase))
                continue;

            return int.TryParse(part.AsSpan(separator + 1).Trim(), out var apt) ? apt : null;
        }

        return null;
    }

    private static SdpAnswerValidationError? ValidateBundle(SdpSessionDescription offer, SdpSessionDescription answer)
    {
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
                {
                    return new SdpAnswerValidationError(
                        SdpAnswerViolation.BundleMidNotOffered, null,
                        $"BUNDLE answer includes mid '{mid}' that was not in the offered group.");
                }
            }
        }
        else if (offerHasBundle)
        {
            return new SdpAnswerValidationError(
                SdpAnswerViolation.BundleMissing, null,
                "Offer required BUNDLE but the answer contains no BUNDLE group.");
        }

        return null;
    }
}
