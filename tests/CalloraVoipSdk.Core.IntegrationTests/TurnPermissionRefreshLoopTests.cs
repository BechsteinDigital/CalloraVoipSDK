using CalloraVoipSdk.Core.Infrastructure.Turn.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The TURN permission refresh loop (RFC 8656 §9): it re-installs the relay's per-peer permissions at ~half the
/// permission lifetime so a long-lived relay path does not lose them, survives a transient refresh failure by
/// retrying, and simply stops on disposal — a permission has no teardown, it lapses on its own once no longer
/// refreshed. The delay is injected so the loop steps deterministically without real waiting.
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

        Task Refresh(CancellationToken ct)
        {
            if (Interlocked.Increment(ref refreshes) == 2) twoRefreshes.TrySetResult();
            return Task.CompletedTask;
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
    public async Task A_transient_refresh_failure_is_retried()
    {
        var calls = 0;
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task Refresh(CancellationToken ct)
        {
            var n = Interlocked.Increment(ref calls);
            if (n == 1)
                throw new InvalidOperationException("transient refresh failure");
            retried.TrySetResult(); // the second attempt proves the loop did not abandon the permissions
            return Task.CompletedTask;
        }

        await using var loop = new TurnPermissionRefreshLoop(
            Refresh, NullLoggerFactory.Instance, permissionLifetimeSeconds: 300,
            // refresh-delay → (fail) retry-backoff → refresh-delay → second attempt.
            delay: ImmediateThenBlock(immediate: 3, new List<TimeSpan>()));
        loop.Start();

        await retried.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(Volatile.Read(ref calls) >= 2);
    }

    [Fact]
    public async Task Dispose_stops_the_loop_and_issues_no_teardown_refresh()
    {
        var calls = 0;
        var firstRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task Refresh(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            firstRefresh.TrySetResult();
            return Task.CompletedTask;
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
