namespace CalloraVoipSdk.Core.Infrastructure.Stun.Ice;

/// <summary>
/// The mutable check-list state of one remote candidate the <see cref="IceNominationDriver"/> tracks
/// (RFC 8445 §6.1.2.6): the candidate itself plus how many connectivity checks it has been given and
/// whether it is finished (nominated or exhausted its attempts).
/// </summary>
internal sealed class IceNominationCandidateState
{
    /// <summary>The remote candidate being checked.</summary>
    public required IceRemoteCandidate Candidate { get; init; }

    /// <summary>How many connectivity checks this candidate has been given so far.</summary>
    public int Attempts { get; set; }

    /// <summary>True once the candidate is nominated or has exhausted its check attempts.</summary>
    public bool Done { get; set; }
}
