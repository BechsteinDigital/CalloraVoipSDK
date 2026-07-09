using CalloraVoipSdk.Core.Application.Media.Ice;
using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Verifies the ICE connectivity-check state machine (RFC 8445 §6.1.4.2 / §7.2.5.3.3):
/// checks are scheduled highest-priority-Waiting first, pair states advance
/// Waiting → InProgress → Succeeded/Failed, the first passing pair is selected, and a
/// completed check unfreezes Frozen pairs that share its foundation.
/// </summary>
public sealed class IceConnectivitySchedulerTests
{
    private static CallIceCandidate Candidate(string type, string address, int port, long priority) => new()
    {
        Foundation = type,
        Component = 1,
        Transport = "UDP",
        Priority = priority,
        Address = address,
        Port = port,
        Type = type,
    };

    private static IceCandidatePair Pair(long priority, string foundation, IceCandidatePairState state) => new()
    {
        Local = Candidate("host", "10.0.0.1", 40000, priority),
        Remote = Candidate("host", "192.0.2.1", 41000, priority),
        Priority = priority,
        Foundation = foundation,
        State = state,
    };

    [Fact]
    public async Task Checks_highest_priority_waiting_pair_first_and_returns_first_success()
    {
        var order = new List<IceCandidatePair>();
        var high = Pair(300, "f-high", IceCandidatePairState.Waiting);
        var mid = Pair(200, "f-mid", IceCandidatePairState.Waiting);
        var low = Pair(100, "f-low", IceCandidatePairState.Waiting);
        var list = new[] { low, high, mid }; // deliberately unordered input

        // Only the mid-priority pair's check succeeds.
        var selected = await IceConnectivityScheduler.RunAsync(
            list,
            (pair, _) =>
            {
                order.Add(pair);
                return Task.FromResult(ReferenceEquals(pair, mid));
            });

        Assert.Same(mid, selected);
        Assert.Equal(new[] { high, mid }, order); // high checked (fails), then mid (succeeds); low untouched
        Assert.Equal(IceCandidatePairState.Failed, high.State);
        Assert.Equal(IceCandidatePairState.Succeeded, mid.State);
        Assert.Equal(IceCandidatePairState.Waiting, low.State);
    }

    [Fact]
    public async Task All_checks_failing_returns_null_and_marks_all_failed()
    {
        var a = Pair(200, "fa", IceCandidatePairState.Waiting);
        var b = Pair(100, "fb", IceCandidatePairState.Waiting);
        var list = new[] { a, b };

        var selected = await IceConnectivityScheduler.RunAsync(list, (_, _) => Task.FromResult(false));

        Assert.Null(selected);
        Assert.Equal(IceCandidatePairState.Failed, a.State);
        Assert.Equal(IceCandidatePairState.Failed, b.State);
    }

    [Fact]
    public async Task Frozen_pair_with_same_foundation_is_unfrozen_and_checked()
    {
        var waiting = Pair(200, "shared", IceCandidatePairState.Waiting);
        var frozen = Pair(100, "shared", IceCandidatePairState.Frozen);
        var list = new[] { waiting, frozen };

        var checkedPairs = new List<IceCandidatePair>();
        // The Waiting pair fails; only the initially Frozen same-foundation pair succeeds.
        var selected = await IceConnectivityScheduler.RunAsync(
            list,
            (pair, _) =>
            {
                checkedPairs.Add(pair);
                return Task.FromResult(ReferenceEquals(pair, frozen));
            });

        Assert.Same(frozen, selected);
        Assert.Equal(new[] { waiting, frozen }, checkedPairs); // frozen only reached after unfreezing
        Assert.Equal(IceCandidatePairState.Failed, waiting.State);
        Assert.Equal(IceCandidatePairState.Succeeded, frozen.State);
    }

    [Fact]
    public async Task Frozen_pair_with_other_foundation_is_never_checked()
    {
        var waiting = Pair(200, "fa", IceCandidatePairState.Waiting);
        var frozen = Pair(100, "fb", IceCandidatePairState.Frozen);
        var list = new[] { waiting, frozen };

        var checkedPairs = new List<IceCandidatePair>();
        var selected = await IceConnectivityScheduler.RunAsync(
            list,
            (pair, _) =>
            {
                checkedPairs.Add(pair);
                return Task.FromResult(false);
            });

        Assert.Null(selected);
        Assert.Equal(new[] { waiting }, checkedPairs); // frozen keeps its state (different foundation)
        Assert.Equal(IceCandidatePairState.Frozen, frozen.State);
    }
}
