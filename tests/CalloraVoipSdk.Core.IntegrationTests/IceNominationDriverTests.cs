using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The controlling agent's ICE candidate-pair check/nomination driver (RFC 8445 §7.2.2/§8.1.1): it visits
/// remote candidates highest-priority first, nominates the first that answers a connectivity check (so
/// nomination is gated on real reachability, not blind priority), carries USE-CANDIDATE on the nominating
/// check, and does nothing when no candidate answers or the set is empty.
/// </summary>
public sealed class IceNominationDriverTests
{
    private static IPEndPoint Ep(int port) => new(IPAddress.Loopback, port);

    [Fact]
    public async Task Nominates_the_highest_priority_candidate_that_answers_a_check()
    {
        var reachable = Ep(5002);
        var unreachableHigher = Ep(5001);
        var nominated = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var useCandidateTargets = new List<IPEndPoint>();

        Task<bool> Check(IPEndPoint target, bool useCandidate, CancellationToken ct)
        {
            if (useCandidate)
                lock (useCandidateTargets) useCandidateTargets.Add(target);
            // Only the reachable (lower-priority) candidate answers; the higher-priority one black-holes.
            return Task.FromResult(target.Equals(reachable));
        }

        await using var driver = new IceNominationDriver(
            [new IceRemoteCandidate(unreachableHigher, Priority: 200), new IceRemoteCandidate(reachable, Priority: 100)],
            Check,
            ep => nominated.TrySetResult(ep),
            NullLoggerFactory.Instance,
            roundDelay: TimeSpan.FromMilliseconds(1));

        driver.Start();

        var winner = await nominated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(reachable, winner);                             // checked reachability, not blind priority
        Assert.Contains(reachable, useCandidateTargets);             // nominating check carried USE-CANDIDATE (§8.1.1)
        Assert.DoesNotContain(unreachableHigher, useCandidateTargets); // the black hole is never nominated
    }

    [Fact]
    public async Task Does_not_nominate_when_no_candidate_answers()
    {
        var nominatedCount = 0;
        await using (var driver = new IceNominationDriver(
            [new IceRemoteCandidate(Ep(6001), 100)],
            (_, _, _) => Task.FromResult(false),
            _ => Interlocked.Increment(ref nominatedCount),
            NullLoggerFactory.Instance,
            maxRounds: 3,
            roundDelay: TimeSpan.FromMilliseconds(1)))
        {
            driver.Start();
            await Task.Delay(200); // let the bounded loop exhaust its rounds
        }

        Assert.Equal(0, Volatile.Read(ref nominatedCount));
    }

    [Fact]
    public async Task Empty_candidate_set_is_a_no_op()
    {
        var nominatedCount = 0;
        await using var driver = new IceNominationDriver(
            [], (_, _, _) => Task.FromResult(true), _ => Interlocked.Increment(ref nominatedCount),
            NullLoggerFactory.Instance);

        driver.Start();
        await Task.Delay(50);

        Assert.Equal(0, Volatile.Read(ref nominatedCount));
    }
}
