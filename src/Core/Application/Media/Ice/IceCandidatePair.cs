using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.Application.Media.Ice;

/// <summary>
/// One local/remote candidate pair on an ICE check list (RFC 8445 §6.1.2). The pair priority
/// and foundation are fixed at construction; <see cref="State"/> and <see cref="Nominated"/>
/// advance as connectivity checks run.
/// </summary>
internal sealed class IceCandidatePair
{
    /// <summary>Local candidate of the pair.</summary>
    public required CallIceCandidate Local { get; init; }

    /// <summary>Remote candidate of the pair.</summary>
    public required CallIceCandidate Remote { get; init; }

    /// <summary>Pair priority (RFC 8445 §6.1.2.3), computed for the agent's role.</summary>
    public required long Priority { get; init; }

    /// <summary>Pair foundation — the combination of the two candidate foundations (§6.1.2.6).</summary>
    public required string Foundation { get; init; }

    /// <summary>Component the pair belongs to (1 = RTP, 2 = RTCP).</summary>
    public int ComponentId => Local.Component;

    /// <summary>Check-list state of the pair (RFC 8445 §6.1.2.6).</summary>
    public IceCandidatePairState State { get; set; } = IceCandidatePairState.Frozen;

    /// <summary>True once the pair has been nominated (RFC 8445 §8).</summary>
    public bool Nominated { get; set; }

    /// <summary>
    /// Computes the pair priority for the given role (RFC 8445 §6.1.2.3):
    /// <c>2^32 * min(G,D) + 2 * max(G,D) + (G &gt; D ? 1 : 0)</c>, where G is the controlling
    /// agent's candidate priority and D the controlled agent's.
    /// </summary>
    public static long ComputePriority(IceRole role, long localPriority, long remotePriority)
    {
        var g = role == IceRole.Controlling ? localPriority : remotePriority;
        var d = role == IceRole.Controlling ? remotePriority : localPriority;
        var min = Math.Min(g, d);
        var max = Math.Max(g, d);
        return (1L << 32) * min + 2 * max + (g > d ? 1 : 0);
    }
}
