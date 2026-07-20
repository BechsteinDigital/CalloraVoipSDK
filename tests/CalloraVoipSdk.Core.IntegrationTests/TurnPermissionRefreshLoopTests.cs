using CalloraVoipSdk.Core.Infrastructure.Turn.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The TURN permission refresh loop (RFC 8656 §9): it re-installs the relay's per-peer permissions at ~half the
/// permission lifetime, and — critically — retries after only the short backoff (not another full interval) when
/// a refresh throws OR reports a partial per-peer failure, so the retry stays inside the permission lifetime. It
/// stops on disposal with no teardown. The delay is injected so the loop steps deterministically.
/// </summary>
public sealed class TurnPermissionRefreshLoopTests
{
    // A delay that records each requested duration; the first <paramref name="immediate"/> calls complete at
    // once (stepping the loop), later calls block until the token is cancelled (parking the loop).
    private static Func<TimeSpan, CancellationToken, Task> ImmediateThenBlock(int immediate, List<TimeSpan> recorded)
    {
        var count = 0;
        return (ts, ct) =>
        {
            lock (recorded) recorded.Add(ts);
            return Interlocked.Increment(ref count) <= immediate ? Task.CompletedTask : Task.Delay(Timeout.Infinite, ct);
        };
    }

    [Fact]
    public async Task Refreshes_at_half_the_permission_lifetime()
    {
        var delays = new List<TimeSpan>();
        var refreshes = 0;
        var twoRefreshes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<bool> Refresh(CancellationToken ct)
        {
            if (Interlocked.Increment(ref refreshes) == 2) twoRefreshes.TrySetResult();
            return Task.FromResult(true);
        }

        await using var loop = new TurnPermissionRefreshLoop(
            Refresh, NullLoggerFactory.Instance, permissionLifetimeSeconds: 300,
            delay: ImmediateThenBlock(immediate: 2, delays));
        loop.Start();

        await twoRefreshes.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lock (delays)
            Assert.Equal(TimeSpan.FromSeconds(150), delays[0]); // half of the 300s permission lifetime
    }

    [Fact]
    public async Task A_thrown_refresh_retries_after_the_backoff_not_another_full_interval()
    {
        // Regression for the retry-timing bug: after a failure at t=150 s the retry must wait only the 5 s backoff
        // (→ t=155 s), NOT another full 150 s interval (→ t=305 s, past the 300 s expiry). With the bug the second
        // attempt needs a third delay (a full interval), which this parked delay function never grants → timeout.
        var delays = new List<TimeSpan>();
        var calls = 0;
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<bool> Refresh(CancellationToken ct)
        {
            if (Interlocked.Increment(ref calls) == 1)
                throw new InvalidOperationException("transient");
            retried.TrySetResult();
            return Task.FromResult(true);
        }

        await using var loop = new TurnPermissionRefreshLoop(
            Refresh, NullLoggerFactory.Instance, permissionLifetimeSeconds: 300,
            delay: ImmediateThenBlock(immediate: 2, delays));
        loop.Start();

        await retried.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lock (delays)
        {
            Assert.Equal(TimeSpan.FromSeconds(150), delays[0]); // first refresh at half-lifetime
            Assert.Equal(TimeSpan.FromSeconds(5), delays[1]);   // retry waits ONLY the backoff
        }
    }

    [Fact]
    public async Task A_partial_peer_failure_shortens_the_next_wait_to_the_backoff()
    {
        // Regression for the swallowed-failure bug: a refresh that reports false (a peer failed) must shorten the
        // next wait to the backoff, not a full interval — otherwise the failed peer is only retried at expiry.
        var delays = new List<TimeSpan>();
        var calls = 0;
        var secondRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<bool> Refresh(CancellationToken ct)
        {
            var n = Interlocked.Increment(ref calls);
            if (n == 2) secondRefresh.TrySetResult();
            return Task.FromResult(n != 1); // first refresh reports a partial failure, then succeeds
        }

        await using var loop = new TurnPermissionRefreshLoop(
            Refresh, NullLoggerFactory.Instance, permissionLifetimeSeconds: 300,
            delay: ImmediateThenBlock(immediate: 2, delays));
        loop.Start();

        await secondRefresh.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lock (delays)
        {
            Assert.Equal(TimeSpan.FromSeconds(150), delays[0]);
            Assert.Equal(TimeSpan.FromSeconds(5), delays[1]); // partial failure → backoff, not another 150 s
        }
    }

    [Fact]
    public async Task Dispose_stops_the_loop_and_issues_no_teardown_refresh()
    {
        var calls = 0;
        var firstRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<bool> Refresh(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            firstRefresh.TrySetResult();
            return Task.FromResult(true);
        }

        var loop = new TurnPermissionRefreshLoop(
            Refresh, NullLoggerFactory.Instance, permissionLifetimeSeconds: 300,
            delay: ImmediateThenBlock(immediate: 1, new List<TimeSpan>()));
        loop.Start();

        await firstRefresh.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var beforeDispose = Volatile.Read(ref calls);
        await loop.DisposeAsync();

        // Unlike the allocation loop (which sends a Refresh-0 teardown), disposal issues no extra refresh —
        // a permission simply lapses. The call count must not grow after disposal.
        await Task.Delay(50);
        Assert.Equal(beforeDispose, Volatile.Read(ref calls));
    }
}
