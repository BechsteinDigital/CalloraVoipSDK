using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The controlling agent's dynamic ICE candidate-pair check/nomination driver (RFC 8445 §7.2.2/§8.1.1,
/// RFC 8838 trickle): it pairs local × remote candidates, nominates the highest pair-priority pair that
/// actually answers a connectivity check (never blind priority), carries USE-CANDIDATE on the nominating
/// check, accepts candidates trickled in after start, re-nominates onto a higher-priority working pair, and
/// never downgrades onto a lower-priority one. Empty sets and non-answering pairs are handled without
/// nominating.
/// </summary>
public sealed class IceNominationDriverTests
{
    private static IPEndPoint Ep(int port) => new(IPAddress.Loopback, port);

    // A single direct local candidate wrapping a check delegate — the shape the driver used before local
    // candidates were modelled explicitly, so these tests exercise the same behaviour over one local path.
    private static IceLocalCandidate Direct(Func<IPEndPoint, bool, CancellationToken, Task<bool>> check)
        => new() { Type = "host", Priority = 1_000_000, Check = check };

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
            [Direct(Reachable(new HashSet<IPEndPoint> { reachable }, useCandidateTargets))],
            [new IceRemoteCandidate(unreachableHigher, Priority: 200), new IceRemoteCandidate(reachable, Priority: 100)],
            (_, ep) => nominated.TrySetResult(ep),
            NullLoggerFactory.Instance,
            roundDelay: TimeSpan.FromMilliseconds(1));

        driver.Start();

        var winner = await nominated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(reachable, winner);                              // checked reachability, not blind priority
        Assert.Contains(reachable, useCandidateTargets);             // nominating check carried USE-CANDIDATE (§8.1.1)
        Assert.DoesNotContain(unreachableHigher, useCandidateTargets); // the black hole is never nominated
    }

    [Fact]
    public async Task Prefers_the_higher_priority_local_candidate_for_the_same_remote()
    {
        var remote = Ep(5602);
        var nominated = new TaskCompletionSource<IceLocalCandidate>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Both local candidates reach the remote; the higher-priority one must win the pair (higher pair
        // priority), proving the driver pairs per local candidate and orders by pair priority.
        Func<IPEndPoint, bool, CancellationToken, Task<bool>> reach = (_, _, _) => Task.FromResult(true);
        var high = new IceLocalCandidate { Type = "host", Priority = 2_000_000, Check = reach };
        var low = new IceLocalCandidate { Type = "relay", Priority = 1_000, Check = reach };

        await using var driver = new IceNominationDriver(
            [low, high], // deliberately unordered
            [new IceRemoteCandidate(remote, Priority: 100)],
            (local, _) => nominated.TrySetResult(local),
            NullLoggerFactory.Instance,
            roundDelay: TimeSpan.FromMilliseconds(1));

        driver.Start();

        var winner = await nominated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("host", winner.Type);
    }

    [Fact]
    public async Task Lower_priority_local_wins_when_the_higher_priority_local_is_unreachable()
    {
        var remote = Ep(5702);
        var nominated = new TaskCompletionSource<IceLocalCandidate>(TaskCreationOptions.RunContinuationsAsynchronously);

        // The higher-priority local never reaches the remote; the lower-priority local does. The higher pair
        // exhausts its attempts, then the driver falls back to the lower-priority working pair (RFC 8445 §6).
        var high = new IceLocalCandidate { Type = "host", Priority = 2_000_000, Check = (_, _, _) => Task.FromResult(false) };
        var low = new IceLocalCandidate { Type = "relay", Priority = 1_000, Check = (_, _, _) => Task.FromResult(true) };

        await using var driver = new IceNominationDriver(
            [high, low],
            [new IceRemoteCandidate(remote, Priority: 100)],
            (local, _) => nominated.TrySetResult(local),
            NullLoggerFactory.Instance,
            maxAttempts: 3,
            roundDelay: TimeSpan.FromMilliseconds(1));

        driver.Start();

        var winner = await nominated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("relay", winner.Type);
    }

    [Fact]
    public async Task A_trickled_candidate_added_after_start_is_checked_and_nominated()
    {
        var trickled = Ep(5102);
        var nominated = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var driver = new IceNominationDriver(
            [Direct(Reachable(new HashSet<IPEndPoint> { trickled }, []))],
            [], // nothing to check at start
            (_, ep) => nominated.TrySetResult(ep),
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
            [Direct(Reachable(new HashSet<IPEndPoint> { low, high }, []))],
            [new IceRemoteCandidate(low, Priority: 100)],
            (_, ep) => { lock (nominations) { nominations.Add(ep); if (ep.Equals(high)) reNominated.TrySetResult(); } },
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
            [Direct(Reachable(new HashSet<IPEndPoint> { high, low }, []))],
            [new IceRemoteCandidate(high, Priority: 200)],
            (_, ep) => { lock (nominations) { nominations.Add(ep); } firstNominated.TrySetResult(); },
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
            [Direct(check)],
            [new IceRemoteCandidate(target, Priority: 100)],
            (_, ep) => { Interlocked.Increment(ref nominations); nominated.TrySetResult(ep); },
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
        // its target locally, and the pair is abandoned after its attempt budget (no phantom nomination).
        var target = Ep(5502);
        var nominatedCount = 0;
        await using (var driver = new IceNominationDriver(
            [Direct((_, useCandidate, _) => Task.FromResult(!useCandidate))], // ordinary true, USE-CANDIDATE never confirmed
            [new IceRemoteCandidate(target, 100)],
            (_, _) => Interlocked.Increment(ref nominatedCount),
            NullLoggerFactory.Instance,
            maxAttempts: 3,
            roundDelay: TimeSpan.FromMilliseconds(1)))
        {
            driver.Start();
            await Task.Delay(200); // let the pair exhaust its attempts
        }

        Assert.Equal(0, Volatile.Read(ref nominatedCount));
    }

    [Fact]
    public async Task Does_not_nominate_when_no_candidate_answers()
    {
        var nominatedCount = 0;
        await using (var driver = new IceNominationDriver(
            [Direct((_, _, _) => Task.FromResult(false))],
            [new IceRemoteCandidate(Ep(6001), 100)],
            (_, _) => Interlocked.Increment(ref nominatedCount),
            NullLoggerFactory.Instance,
            maxAttempts: 3,
            roundDelay: TimeSpan.FromMilliseconds(1)))
        {
            driver.Start();
            await Task.Delay(200); // let the pair exhaust its attempts
        }

        Assert.Equal(0, Volatile.Read(ref nominatedCount));
    }

    [Fact]
    public async Task Empty_candidate_set_is_a_no_op()
    {
        var nominatedCount = 0;
        await using var driver = new IceNominationDriver(
            [Direct((_, _, _) => Task.FromResult(true))], [], (_, _) => Interlocked.Increment(ref nominatedCount),
            NullLoggerFactory.Instance);

        driver.Start();
        await Task.Delay(50);

        Assert.Equal(0, Volatile.Read(ref nominatedCount));
    }
}
