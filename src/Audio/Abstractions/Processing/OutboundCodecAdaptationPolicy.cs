namespace CalloraVoipSdk.Audio.Abstractions.Processing;

/// <summary>
/// Decides whether a symmetric UAC should switch the codec it sends with to match an inbound
/// frame's payload type. RFC 3264 §5.1 / RFC 3550: a peer must only send a codec the far end
/// agreed to receive. Adapting blindly to any inbound static payload type (0/8/9) would let a
/// stray or spoofed packet push the sender onto a codec that was never negotiated for this leg
/// (issue #18, A2). This policy therefore only permits adaptation to a payload type that is part
/// of the negotiated set for the leg — the SDP payload-type→codec map plus the originally
/// negotiated payload type. Both platform devices route through this single policy so their
/// send-adaptation behaviour is identical and RFC-correct. Pure and stateless for unit testing.
/// </summary>
public static class OutboundCodecAdaptationPolicy
{
    /// <summary>
    /// Evaluates whether the outbound codec should adapt to <paramref name="inboundPayloadType"/>.
    /// </summary>
    /// <param name="inboundPayloadType">Payload type of the just-received inbound frame.</param>
    /// <param name="currentOutboundPayloadType">The payload type currently used for sending.</param>
    /// <param name="negotiatedPayloadType">The originally negotiated primary payload type.</param>
    /// <param name="negotiatedPayloadTypes">
    /// The payload types present in the negotiated SDP map for this leg; may be empty.
    /// </param>
    /// <returns>
    /// An <see cref="OutboundCodecAdaptationDecision"/>; <see cref="OutboundCodecAdaptationDecision.NoChange"/>
    /// when the inbound payload type is out of range, not negotiated, or already active.
    /// </returns>
    public static OutboundCodecAdaptationDecision Evaluate(
        int inboundPayloadType,
        int currentOutboundPayloadType,
        int negotiatedPayloadType,
        IEnumerable<int> negotiatedPayloadTypes)
    {
        ArgumentNullException.ThrowIfNull(negotiatedPayloadTypes);

        // Only the 7-bit RTP payload-type space is valid (RFC 3550 §5.1).
        if (inboundPayloadType is < 0 or > 127)
            return OutboundCodecAdaptationDecision.NoChange;

        // Fail-closed: never switch to a codec the far end did not negotiate for this leg. The
        // primary negotiated PT is checked first so the common path never enumerates the map.
        var isNegotiated = inboundPayloadType == negotiatedPayloadType
            || Contains(negotiatedPayloadTypes, inboundPayloadType);
        if (!isNegotiated)
            return OutboundCodecAdaptationDecision.NoChange;

        if (inboundPayloadType == currentOutboundPayloadType)
            return OutboundCodecAdaptationDecision.NoChange;

        return OutboundCodecAdaptationDecision.Adapt(inboundPayloadType);
    }

    private static bool Contains(IEnumerable<int> values, int target)
    {
        // Prefer O(1) membership when the caller already holds a keyed collection (the dictionary
        // KeyCollection implements ICollection<int>), otherwise fall back to a linear scan.
        if (values is ICollection<int> collection)
            return collection.Contains(target);

        foreach (var value in values)
        {
            if (value == target)
                return true;
        }

        return false;
    }
}
