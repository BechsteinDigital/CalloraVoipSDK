using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The controlling agent's dynamic ICE candidate-pair check/nomination driver (RFC 8445 §7.2.2/§8.1.1,
/// RFC 8838 trickle): it nominates the highest-priority candidate that actually answers a connectivity check
/// (never blind priority), carries USE-CANDIDATE on the nominating check, accepts candidates trickled in
/// after start, re-nominates onto a higher-priority working pair, and never downgrades onto a lower-priority
/// one. Empty sets and non-answering candidates are handled without nominating.
/// </summary>
public sealed class IceNominationDriverTests
{
    private static IPEndPoint Ep(int port) => new(IPAddress.Loopback, port);

    // A check delegate that answers only for endpoints in the reachable set; records the USE-CANDIDATE targets.
    private static Func<IPEndPoint, bool, CancellationToken, Task<bool>> Reachable(
        ISet<IPEndPoint> reachable, List<IPEndPoint> useCandidateTargets)
    {
        return (target, useCandidate, _) =>
        {
            if (useCandidate)
                lock (useCandidateTargets) useCandidateTargets.Add(target);
            return Task.FromResult(reachable.Contains(target));
        };
    }

    [Fact]
    public async Task Nominates_the_highest_priority_candidate_that_answers_a_check()
    {
        var reachable = Ep(5002);
        var unreachableHigher = Ep(5001);
        var nominated = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var useCandidateTargets = new List<IPEndPoint>();

        await using var driver = new IceNominationDriver(
            [new IceRemoteCandidate(unreachableHigher, Priority: 200), new IceRemoteCandidate(reachable, Priority: 100)],
            Reachable(new HashSet<IPEndPoint> { reachable }, useCandidateTargets),
            ep => nominated.TrySetResult(ep),
            NullLoggerFactory.Instance,
            roundDelay: TimeSpan.FromMilliseconds(1));

        driver.Start();

        var winner = await nominated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(reachable, winner);                              // checked reachability, not blind priority
        Assert.Contains(reachable, useCandidateTargets);             // nominating check carried USE-CANDIDATE (§8.1.1)
        Assert.DoesNotContain(unreachableHigher, useCandidateTargets); // the black hole is never nominated
    }

    [Fact]
    public async Task A_trickled_candidate_added_after_start_is_checked_and_nominated()
    {
        var trickled = Ep(5102);
        var nominated = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var driver = new IceNominationDriver(
            [], // nothing to check at start
            Reachable(new HashSet<IPEndPoint> { trickled }, []),
            ep => nominated.TrySetResult(ep),
            NullLoggerFactory.Instance,
            roundDelay: TimeSpan.FromMilliseconds(5));

        driver.Start();
        driver.AddCandidate(new IceRemoteCandidate(trickled, Priority: 100)); // RFC 8838 trickle after start

        Assert.Equal(trickled, await nominated.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task A_higher_priority_trickled_candidate_re_nominates()
    {
        var low = Ep(5202);
        var high = Ep(5201);
        var nominations = new List<IPEndPoint>();
        var reNominated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var driver = new IceNominationDriver(
            [new IceRemoteCandidate(low, Priority: 100)],
            Reachable(new HashSet<IPEndPoint> { low, high }, []),
            ep => { lock (nominations) { nominations.Add(ep); if (ep.Equals(high)) reNominated.TrySetResult(); } },
            NullLoggerFactory.Instance,
            roundDelay: TimeSpan.FromMilliseconds(5));

        driver.Start();
        // Let the low pair be nominated first, then trickle a higher-priority working pair.
        await Task.Delay(50);
        driver.AddCandidate(new IceRemoteCandidate(high, Priority: 200));

        await reNominated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lock (nominations)
        {
            Assert.Equal(low, nominations[0]);      // highest-priority working pair known at the time
            Assert.Equal(high, nominations[^1]);    // upgraded onto the higher-priority working pair (§8)
        }
    }

    [Fact]
    public async Task A_lower_priority_trickled_candidate_does_not_override_a_working_pair()
    {
        var high = Ep(5301);
        var low = Ep(5302);
        var nominations = new List<IPEndPoint>();
        var firstNominated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var driver = new IceNominationDriver(
            [new IceRemoteCandidate(high, Priority: 200)],
            Reachable(new HashSet<IPEndPoint> { high, low }, []),
            ep => { lock (nominations) { nominations.Add(ep); } firstNominated.TrySetResult(); },
            NullLoggerFactory.Instance,
            roundDelay: TimeSpan.FromMilliseconds(5));

        driver.Start();
        await firstNominated.Task.WaitAsync(TimeSpan.FromSeconds(5)); // high nominated
        driver.AddCandidate(new IceRemoteCandidate(low, Priority: 100)); // lower priority, even though reachable
        await Task.Delay(150); // give the driver time to (not) act on it

        lock (nominations)
        {
            Assert.Equal(new[] { high }, nominations); // never downgraded onto the lower-priority pair
        }
    }

    [Fact]
    public async Task Does_not_nominate_until_the_use_candidate_check_is_confirmed()
    {
        // The ordinary check validates the path, but the nominating (USE-CANDIDATE) check's response is lost
        // the first two times — the pair must NOT be adopted until that nominating check is itself confirmed
        // (RFC 8445 §8.1.1). The driver retries the nomination (re-validating with a fresh ordinary check
        // before each USE-CANDIDATE retry) rather than switching locally, and nominates exactly once.
        var target = Ep(5402);
        var ordinaryChecks = 0;
        var useCandidateAttempts = 0;
        var nominations = 0;
        var nominated = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);

        Func<IPEndPoint, bool, CancellationToken, Task<bool>> check = (_, useCandidate, _) =>
        {
            if (!useCandidate)
            {
                Interlocked.Increment(ref ordinaryChecks);
                return Task.FromResult(true);                     // the path always works
            }
            var attempt = Interlocked.Increment(ref useCandidateAttempts);
            return Task.FromResult(attempt >= 3);                 // USE-CANDIDATE confirmed only on the 3rd try
        };

        await using var driver = new IceNominationDriver(
            [new IceRemoteCandidate(target, Priority: 100)],
            check,
            ep => { Interlocked.Increment(ref nominations); nominated.TrySetResult(ep); },
            NullLoggerFactory.Instance,
            maxAttempts: 5,
            roundDelay: TimeSpan.FromMilliseconds(1));

        driver.Start();

        Assert.Equal(target, await nominated.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(Volatile.Read(ref useCandidateAttempts) >= 3); // it retried the nominating check until confirmed
        Assert.True(Volatile.Read(ref ordinaryChecks) >= 3);       // and re-validated the pair before each retry
        await Task.Delay(50);                                       // give any erroneous second nomination a chance to fire
        Assert.Equal(1, Volatile.Read(ref nominations));           // nominated exactly once, only after confirmation
    }

    [Fact]
    public async Task Does_not_nominate_when_the_use_candidate_check_is_never_confirmed()
    {
        // The path validates but the nominating check is never confirmed: the controlling side must not switch
        // its target locally, and the candidate is abandoned after its attempt budget (no phantom nomination).
        var target = Ep(5502);
        var nominatedCount = 0;
        await using (var driver = new IceNominationDriver(
            [new IceRemoteCandidate(target, 100)],
            (_, useCandidate, _) => Task.FromResult(!useCandidate), // ordinary check true, USE-CANDIDATE never confirmed
            _ => Interlocked.Increment(ref nominatedCount),
            NullLoggerFactory.Instance,
            maxAttempts: 3,
            roundDelay: TimeSpan.FromMilliseconds(1)))
        {
            driver.Start();
            await Task.Delay(200); // let the candidate exhaust its attempts
        }

        Assert.Equal(0, Volatile.Read(ref nominatedCount));
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
            maxAttempts: 3,
            roundDelay: TimeSpan.FromMilliseconds(1)))
        {
            driver.Start();
            await Task.Delay(200); // let the candidate exhaust its attempts
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
