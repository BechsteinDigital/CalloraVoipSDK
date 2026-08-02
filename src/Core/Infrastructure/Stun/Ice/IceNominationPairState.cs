namespace CalloraVoipSdk.Core.Infrastructure.Stun.Ice;

/// <summary>
/// The mutable check-list state of one candidate pair the <see cref="IceNominationDriver"/> tracks
/// (RFC 8445 §6.1.2): the local × remote candidate pair, computed pair priority (§6.1.2.3), explicit
/// checklist phase, outstanding transaction count, validation and nomination state.
/// </summary>
internal sealed class IceNominationPairState
{
    /// <summary>The local candidate (send path) of this pair.</summary>
    public required IceLocalCandidate Local { get; init; }

    /// <summary>The remote candidate being checked.</summary>
    public required IceRemoteCandidate Remote { get; set; }

    /// <summary>The pair priority (RFC 8445 §6.1.2.3), ordering which pair is checked next.</summary>
    public required long PairPriority { get; set; }

    /// <summary>Current checklist phase.</summary>
    public IceNominationPairPhase Phase { get; set; } = IceNominationPairPhase.Frozen;

    /// <summary>Whether this pair's ordinary connectivity check has already been scheduled.</summary>
    public bool OrdinaryScheduled { get; set; }

    /// <summary>Number of ordinary/triggered transactions currently outstanding for the pair.</summary>
    public int PendingChecks { get; set; }

    /// <summary>Whether at least one authenticated transaction validated the pair.</summary>
    public bool Validated { get; set; }

    /// <summary>Number of failed USE-CANDIDATE transactions for this pair.</summary>
    public int NominationAttempts { get; set; }

    /// <summary>Generation used to invalidate a queued nomination when a higher pair arrives first.</summary>
    public int NominationGeneration { get; set; }
}
