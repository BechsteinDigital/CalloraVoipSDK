namespace CalloraVoipSdk.Core.Application.Media.Ice;

/// <summary>
/// Outcome of resolving an ICE role conflict: the role the agent holds afterwards and whether
/// it must reject the triggering connectivity check with a 487 (Role Conflict) error response.
/// </summary>
internal readonly record struct IceRoleConflictResolution(IceRole Role, bool Reject487);

/// <summary>
/// Resolves ICE role conflicts on an inbound connectivity check (RFC 8445 §7.3.1.1). A conflict
/// exists when the peer's check signals the same role this agent holds. The agent with the
/// larger-or-equal tie-breaker takes/keeps the controlling role; the other switches to
/// controlled. When this agent keeps its role against a peer that claims it, the check is
/// rejected with 487 so the peer switches.
/// </summary>
internal static class IceRoleConflict
{
    /// <param name="currentRole">The role this agent currently holds.</param>
    /// <param name="peerSignaledControlling">
    /// <see langword="true"/> when the inbound check carried ICE-CONTROLLING (the peer claims the
    /// controlling role); <see langword="false"/> for ICE-CONTROLLED.
    /// </param>
    /// <param name="ownTieBreaker">This agent's tie-breaker.</param>
    /// <param name="peerTieBreaker">The tie-breaker carried in the peer's role attribute.</param>
    public static IceRoleConflictResolution Resolve(
        IceRole currentRole,
        bool peerSignaledControlling,
        ulong ownTieBreaker,
        ulong peerTieBreaker)
    {
        // No conflict: the peer signalled the opposite role.
        if (currentRole == IceRole.Controlling && !peerSignaledControlling)
            return new IceRoleConflictResolution(IceRole.Controlling, Reject487: false);
        if (currentRole == IceRole.Controlled && peerSignaledControlling)
            return new IceRoleConflictResolution(IceRole.Controlled, Reject487: false);

        var ownWins = ownTieBreaker >= peerTieBreaker;

        // Both agents claim controlling: the winner keeps controlling and rejects (peer switches).
        if (currentRole == IceRole.Controlling)
        {
            return ownWins
                ? new IceRoleConflictResolution(IceRole.Controlling, Reject487: true)
                : new IceRoleConflictResolution(IceRole.Controlled, Reject487: false);
        }

        // Both agents claim controlled: the winner switches to controlling; the loser keeps
        // controlled and rejects (peer switches to controlling).
        return ownWins
            ? new IceRoleConflictResolution(IceRole.Controlling, Reject487: false)
            : new IceRoleConflictResolution(IceRole.Controlled, Reject487: true);
    }
}
