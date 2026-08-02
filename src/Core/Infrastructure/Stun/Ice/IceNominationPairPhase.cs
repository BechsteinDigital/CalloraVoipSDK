namespace CalloraVoipSdk.Core.Infrastructure.Stun.Ice;

/// <summary>Runtime checklist phase of one live media candidate pair (RFC 8445 §6.1.2.6 and §8).</summary>
internal enum IceNominationPairPhase
{
    Frozen,
    Waiting,
    InProgress,
    Succeeded,
    Failed,
    Nominating,
    Nominated,
}
