namespace CalloraVoipSdk.Core.Application.Media.Ice;

/// <summary>
/// Outcome of evaluating an inbound ICE connectivity-check Binding request (RFC 8445 §7.3).
/// </summary>
/// <param name="Accepted">
/// <see langword="true"/> when the check is accepted and a Success response (carrying
/// XOR-MAPPED-ADDRESS) should be sent.
/// </param>
/// <param name="Reject487">
/// <see langword="true"/> when the check must be rejected with a 487 Role Conflict error response
/// so the peer switches role (RFC 8445 §7.3.1.1).
/// </param>
/// <param name="RoleAfter">This agent's role after role-conflict resolution.</param>
/// <param name="NominatePair">
/// <see langword="true"/> when the peer's USE-CANDIDATE nominates this pair and this agent is the
/// controlled one (RFC 8445 §7.3.1.5).
/// </param>
internal readonly record struct IceInboundCheckResult(
    bool Accepted,
    bool Reject487,
    IceRole RoleAfter,
    bool NominatePair);

/// <summary>
/// Evaluates the ICE semantics of an inbound STUN Binding request received on a media leg
/// (RFC 8445 §7.3): confirms the USERNAME targets this agent, resolves a role conflict from the
/// peer's ICE-CONTROLLING / ICE-CONTROLLED attribute (§7.3.1.1), and reads USE-CANDIDATE
/// (§7.3.1.5). Pure decision logic — the wire layer verifies MESSAGE-INTEGRITY and builds the
/// response; peer-reflexive learning and triggered checks are handled by the transport layer.
/// </summary>
internal static class IceInboundCheckEvaluator
{
    /// <summary>
    /// Evaluates one inbound check.
    /// </summary>
    /// <param name="localUfrag">This agent's local ICE username fragment.</param>
    /// <param name="requestUsername">The USERNAME attribute carried in the request.</param>
    /// <param name="currentRole">The role this agent currently holds.</param>
    /// <param name="ownTieBreaker">This agent's tie-breaker.</param>
    /// <param name="peerControlling">
    /// <see langword="true"/> when the request carried ICE-CONTROLLING, <see langword="false"/>
    /// for ICE-CONTROLLED, <see langword="null"/> when neither was present.
    /// </param>
    /// <param name="peerTieBreaker">The tie-breaker carried in the peer's role attribute.</param>
    /// <param name="hasUseCandidate">Whether the request carried USE-CANDIDATE.</param>
    public static IceInboundCheckResult Evaluate(
        string localUfrag,
        string requestUsername,
        IceRole currentRole,
        ulong ownTieBreaker,
        bool? peerControlling,
        ulong peerTieBreaker,
        bool hasUseCandidate)
    {
        // §7.3 short-term credentials: an inbound check's USERNAME is "{our-ufrag}:{peer-ufrag}".
        if (!UsernameTargetsUs(localUfrag, requestUsername))
            return new IceInboundCheckResult(Accepted: false, Reject487: false, currentRole, NominatePair: false);

        var role = currentRole;
        if (peerControlling is bool peerControllingRole)
        {
            var resolution = IceRoleConflict.Resolve(currentRole, peerControllingRole, ownTieBreaker, peerTieBreaker);
            if (resolution.Reject487)
                return new IceInboundCheckResult(Accepted: false, Reject487: true, resolution.Role, NominatePair: false);
            role = resolution.Role;
        }

        // §7.3.1.5: only a controlling peer sets USE-CANDIDATE; a controlled agent acts on it.
        var nominate = hasUseCandidate && role == IceRole.Controlled;
        return new IceInboundCheckResult(Accepted: true, Reject487: false, role, nominate);
    }

    // The USERNAME's first half (before ':') is the destination ufrag, which for an inbound
    // check must be this agent's local ufrag.
    private static bool UsernameTargetsUs(string localUfrag, string requestUsername)
    {
        if (string.IsNullOrEmpty(localUfrag) || string.IsNullOrEmpty(requestUsername))
            return false;

        var colon = requestUsername.IndexOf(':');
        if (colon <= 0)
            return false;

        return requestUsername.AsSpan(0, colon).SequenceEqual(localUfrag);
    }
}
