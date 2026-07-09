using CalloraVoipSdk.Core.Infrastructure.Stun.Attributes;

namespace CalloraVoipSdk.Core.Infrastructure.Stun.Client;

/// <summary>
/// Builds the RFC 8445 §7.2.2 attributes carried in an ICE connectivity-check Binding request:
/// PRIORITY (the priority the peer would assign a peer-reflexive candidate learned from the
/// check), the role attribute (ICE-CONTROLLING or ICE-CONTROLLED) with the agent's tie-breaker,
/// and USE-CANDIDATE when the controlling agent nominates the pair. These precede
/// MESSAGE-INTEGRITY, which the client appends when encoding.
/// </summary>
internal static class StunIceCheckAttributes
{
    /// <param name="priority">PRIORITY value for the check (RFC 8445 §7.2.2).</param>
    /// <param name="isControlling">Whether this agent currently holds the controlling role.</param>
    /// <param name="tieBreaker">The agent's 64-bit tie-breaker.</param>
    /// <param name="useCandidate">
    /// When <see langword="true"/> and the agent is controlling, adds USE-CANDIDATE to nominate
    /// the pair (ignored for a controlled agent, which never nominates).
    /// </param>
    public static IReadOnlyList<StunAttribute> Build(
        uint priority,
        bool isControlling,
        ulong tieBreaker,
        bool useCandidate)
    {
        var attributes = new List<StunAttribute>(3)
        {
            new PriorityAttribute { Value = priority },
            isControlling
                ? new IceControllingAttribute { TieBreaker = tieBreaker }
                : new IceControlledAttribute { TieBreaker = tieBreaker },
        };

        if (useCandidate && isControlling)
            attributes.Add(new UseCandidateAttribute());

        return attributes;
    }
}
