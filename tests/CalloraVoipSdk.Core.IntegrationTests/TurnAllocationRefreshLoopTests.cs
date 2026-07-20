using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;
using CalloraVoipSdk.Core.Infrastructure.Turn.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The TURN allocation keepalive loop (RFC 8656 §3.9): it refreshes at ~half the granted lifetime, threads the
/// server's rotated credentials into the next refresh, best-effort deletes the allocation (Refresh lifetime 0)
/// on disposal, stops when the server drops the allocation, and survives a transient refresh failure by
/// retrying. Clock/delay are injected so the loop steps deterministically without real waiting.
/// </summary>
public sealed class TurnAllocationRefreshLoopTests
{
    private static StunCredentials Creds(string nonce) =>
        new() { Username = "user", Password = "pass", Realm = "realm", Nonce = nonce };

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
    public async Task Refreshes_at_half_the_granted_lifetime_threads_credentials_and_deletes_on_dispose()
    {
        var initial = Creds("n1");
        var rotated1 = Creds("n2");
        var rotated2 = Creds("n3");
        var delays = new List<TimeSpan>();
        var calls = new List<(StunCredentials? Credentials, uint Lifetime)>();
        var twoRefreshes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<TurnRefreshResult> Refresh(StunCredentials? creds, uint lifetime, CancellationToken ct)
        {
            int n;
            lock (calls) { calls.Add((creds, lifetime)); n = calls.Count; }
            if (n == 2) twoRefreshes.TrySetResult();
            var next = n == 1 ? rotated1 : rotated2;
            return Task.FromResult(new TurnRefreshResult { LifetimeSeconds = 200, EffectiveCredentials = next });
        }

        var loop = new TurnAllocationRefreshLoop(
            Refresh, initial, grantedLifetimeSeconds: 200, NullLoggerFactory.Instance,
            delay: ImmediateThenBlock(immediate: 2, delays));
        loop.Start();

        await twoRefreshes.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lock (delays)
            Assert.Equal(TimeSpan.FromSeconds(100), delays[0]); // half of the 200s granted lifetime
        lock (calls)
        {
            Assert.Equal(200u, calls[0].Lifetime);              // requests the granted lifetime by default
            Assert.Same(initial, calls[0].Credentials);         // first refresh carries the initial credentials
            Assert.Same(rotated1, calls[1].Credentials);        // the server's rotated nonce is threaded forward
        }

        await loop.DisposeAsync();

        lock (calls)
        {
            var teardown = calls[^1];
            Assert.Equal(0u, teardown.Lifetime);                // Refresh lifetime 0 deletes the allocation
            Assert.Same(rotated2, teardown.Credentials);        // with the latest credentials
        }
    }

    [Fact]
    public async Task Stops_and_skips_teardown_when_the_server_drops_the_allocation()
    {
        var calls = new List<uint>();
        var dropped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<TurnRefreshResult> Refresh(StunCredentials? creds, uint lifetime, CancellationToken ct)
        {
            lock (calls) calls.Add(lifetime);
            dropped.TrySetResult();
            return Task.FromResult(new TurnRefreshResult { LifetimeSeconds = 0 }); // server let the allocation go
        }

        var loop = new TurnAllocationRefreshLoop(
            Refresh, Creds("n1"), grantedLifetimeSeconds: 200, NullLoggerFactory.Instance,
            delay: ImmediateThenBlock(immediate: 1, new List<TimeSpan>()));
        loop.Start();

        await dropped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await loop.DisposeAsync(); // loop already returned; teardown must be skipped

        lock (calls)
        {
            Assert.Single(calls);          // exactly the one refresh that reported lifetime 0
            Assert.DoesNotContain(0u, calls); // and no Refresh-0 teardown was sent (the allocation is already gone)
        }
    }

    [Fact]
    public async Task A_transient_refresh_failure_is_retried()
    {
        var calls = 0;
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<TurnRefreshResult> Refresh(StunCredentials? creds, uint lifetime, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref calls);
            if (n == 1)
                throw new InvalidOperationException("transient refresh failure");
            retried.TrySetResult(); // the second attempt proves the loop did not abandon the allocation
            return Task.FromResult(new TurnRefreshResult { LifetimeSeconds = 200, EffectiveCredentials = creds });
        }

        var loop = new TurnAllocationRefreshLoop(
            Refresh, Creds("n1"), grantedLifetimeSeconds: 200, NullLoggerFactory.Instance,
            // refresh-delay → (fail) retry-backoff → refresh-delay → second attempt.
            delay: ImmediateThenBlock(immediate: 3, new List<TimeSpan>()));
        loop.Start();

        await retried.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(Volatile.Read(ref calls) >= 2);

        await loop.DisposeAsync();
    }

    [Fact]
    public async Task A_failed_refresh_retries_after_the_backoff_not_another_full_interval()
    {
        // Regression for the retry-timing bug: after a failure the retry must wait only the 5 s backoff, not
        // another full half-lifetime interval that would push the second attempt past the allocation expiry.
        var delays = new List<TimeSpan>();
        var calls = 0;
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<TurnRefreshResult> Refresh(StunCredentials? creds, uint lifetime, CancellationToken ct)
        {
            if (Interlocked.Increment(ref calls) == 1)
                throw new InvalidOperationException("transient");
            retried.TrySetResult();
            return Task.FromResult(new TurnRefreshResult { LifetimeSeconds = 200, EffectiveCredentials = creds });
        }

        var loop = new TurnAllocationRefreshLoop(
            Refresh, Creds("n1"), grantedLifetimeSeconds: 200, NullLoggerFactory.Instance,
            delay: ImmediateThenBlock(immediate: 2, delays));
        loop.Start();

        await retried.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lock (delays)
        {
            Assert.Equal(TimeSpan.FromSeconds(100), delays[0]); // first refresh at half of the 200 s lifetime
            Assert.Equal(TimeSpan.FromSeconds(5), delays[1]);   // retry waits ONLY the backoff
        }

        await loop.DisposeAsync();
    }

    [Fact]
    public async Task Tears_down_the_allocation_even_when_never_started()
    {
        var calls = new List<uint>();

        Task<TurnRefreshResult> Refresh(StunCredentials? creds, uint lifetime, CancellationToken ct)
        {
            lock (calls) calls.Add(lifetime);
            return Task.FromResult(new TurnRefreshResult { LifetimeSeconds = 0 });
        }

        var loop = new TurnAllocationRefreshLoop(
            Refresh, Creds("n1"), grantedLifetimeSeconds: 200, NullLoggerFactory.Instance);

        await loop.DisposeAsync(); // never Start()ed — the gathered allocation must still be deleted

        lock (calls)
            Assert.Equal(new[] { 0u }, calls); // a single Refresh-0 teardown, nothing else
    }
}
