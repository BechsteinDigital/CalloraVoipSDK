namespace CalloraVoipSdk.Core.Application.Media.Ice;

/// <summary>
/// Drives an ordered ICE check list as a connectivity-check state machine (RFC 8445 §6.1.4.2
/// ordinary check scheduling and §7.2.5.3.3 state advancement). Each iteration takes the
/// highest-priority pair still in <see cref="IceCandidatePairState.Waiting"/>, marks it
/// <see cref="IceCandidatePairState.InProgress"/>, runs one connectivity check through the
/// supplied delegate, and records <see cref="IceCandidatePairState.Succeeded"/> or
/// <see cref="IceCandidatePairState.Failed"/>. A completed check unfreezes the remaining
/// Frozen pairs that share the same foundation.
/// <para>
/// This package (ICE I3) selects the first pair that passes a check — which, because pairs are
/// visited in descending priority order, is the highest-priority valid pair. Regular nomination
/// via USE-CANDIDATE (RFC 8445 §8) and peer-reflexive discovery are later packages.
/// </para>
/// </summary>
internal static class IceConnectivityScheduler
{
    /// <summary>
    /// Runs the check list until a pair passes a connectivity check or no checkable pair
    /// remains. Mutates the <see cref="IceCandidatePair.State"/> of the pairs as it advances.
    /// </summary>
    /// <param name="checkList">
    /// The ordered check list produced by <see cref="IceCheckList.Create"/>; pair states are
    /// advanced in place.
    /// </param>
    /// <param name="check">
    /// Runs one connectivity check (including any transport-level retransmissions) for a pair and
    /// returns <see langword="true"/> when it succeeds.
    /// </param>
    /// <param name="ct">Cancellation token to abort the scheduling loop.</param>
    /// <returns>The selected valid pair, or <see langword="null"/> when every check failed.</returns>
    public static async Task<IceCandidatePair?> RunAsync(
        IReadOnlyList<IceCandidatePair> checkList,
        Func<IceCandidatePair, CancellationToken, Task<bool>> check,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(checkList);
        ArgumentNullException.ThrowIfNull(check);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var pair = NextWaiting(checkList);
            if (pair is null)
                break;

            pair.State = IceCandidatePairState.InProgress;

            if (await check(pair, ct).ConfigureAwait(false))
            {
                pair.State = IceCandidatePairState.Succeeded;
                return pair;
            }

            pair.State = IceCandidatePairState.Failed;
            Unfreeze(checkList, pair.Foundation);
        }

        return null;
    }

    // RFC 8445 §6.1.4.2: the ordinary check is sent for the highest-priority pair in the
    // Waiting state.
    private static IceCandidatePair? NextWaiting(IReadOnlyList<IceCandidatePair> checkList)
    {
        IceCandidatePair? best = null;
        foreach (var pair in checkList)
        {
            if (pair.State != IceCandidatePairState.Waiting)
                continue;
            if (best is null || pair.Priority > best.Priority)
                best = pair;
        }

        return best;
    }

    // RFC 8445 §7.2.5.3.3: once a check completes, the remaining Frozen pairs with the same
    // foundation become Waiting so they can be scheduled next. With a single RTP component and
    // unique per-pair foundations this is inert; it becomes active once a second component
    // (RTCP, a later package) shares foundations across pairs.
    private static void Unfreeze(IReadOnlyList<IceCandidatePair> checkList, string foundation)
    {
        foreach (var pair in checkList)
        {
            if (pair.State == IceCandidatePairState.Frozen
                && string.Equals(pair.Foundation, foundation, StringComparison.Ordinal))
            {
                pair.State = IceCandidatePairState.Waiting;
            }
        }
    }
}
