namespace CalloraVoipSdk.Core.Infrastructure.Stun.Ice;

/// <summary>
/// The mutable check-list state of one candidate pair the <see cref="IceNominationDriver"/> tracks
/// (RFC 8445 §6.1.2): the local × remote candidate pair and its computed pair priority (§6.1.2.3), plus how
/// many connectivity checks it has been given and whether it is finished (nominated or exhausted).
/// </summary>
internal sealed class IceNominationPairState
{
    /// <summary>The local candidate (send path) of this pair.</summary>
    public required IceLocalCandidate Local { get; init; }

    /// <summary>The remote candidate being checked.</summary>
    public required IceRemoteCandidate Remote { get; init; }

    /// <summary>The pair priority (RFC 8445 §6.1.2.3), ordering which pair is checked next.</summary>
    public required long PairPriority { get; init; }

    /// <summary>How many connectivity checks this pair has been given so far.</summary>
    public int Attempts { get; set; }

    /// <summary>True once the pair is nominated or has exhausted its check attempts.</summary>
    public bool Done { get; set; }
}
