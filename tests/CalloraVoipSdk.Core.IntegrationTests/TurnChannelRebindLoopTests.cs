using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;
using CalloraVoipSdk.Core.Infrastructure.Turn.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The TURN channel rebind loop (RFC 8656 §12): it re-issues ChannelBind at ~half the channel lifetime so a
/// long-lived relay data path does not lose its channel binding, threads the server's rotated credentials into
/// the next re-bind, survives a transient failure by retrying, and simply stops on disposal — a channel binding
/// has no teardown, it lapses on its own. The delay is injected so the loop steps deterministically.
/// </summary>
public sealed class TurnChannelRebindLoopTests
{
    private static StunCredentials Creds(string nonce) =>
        new() { Username = "user", Password = "pass", Realm = "realm", Nonce = nonce };

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
    public async Task Rebinds_at_half_the_channel_lifetime_and_threads_credentials()
    {
        var initial = Creds("n1");
        var rotated1 = Creds("n2");
        var rotated2 = Creds("n3");
        var delays = new List<TimeSpan>();
        var calls = new List<StunCredentials?>();
        var twoRebinds = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<StunCredentials?> Rebind(StunCredentials? creds, CancellationToken ct)
        {
            int n;
            lock (calls) { calls.Add(creds); n = calls.Count; }
            if (n == 2) twoRebinds.TrySetResult();
            return Task.FromResult<StunCredentials?>(n == 1 ? rotated1 : rotated2);
        }

        await using var loop = new TurnChannelRebindLoop(
            Rebind, initial, NullLoggerFactory.Instance, channelLifetimeSeconds: 600,
            delay: ImmediateThenBlock(immediate: 2, delays));
        loop.Start();

        await twoRebinds.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lock (delays)
            Assert.Equal(TimeSpan.FromSeconds(300), delays[0]); // half of the 600s channel binding lifetime
        lock (calls)
        {
            Assert.Same(initial, calls[0]);   // first re-bind carries the initial credentials
            Assert.Same(rotated1, calls[1]);  // the server's rotated nonce is threaded forward
        }
    }

    [Fact]
    public async Task A_transient_rebind_failure_is_retried()
    {
        var calls = 0;
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<StunCredentials?> Rebind(StunCredentials? creds, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref calls);
            if (n == 1)
                throw new InvalidOperationException("transient rebind failure");
            retried.TrySetResult(); // the second attempt proves the loop did not abandon the channel
            return Task.FromResult<StunCredentials?>(creds);
        }

        await using var loop = new TurnChannelRebindLoop(
            Rebind, Creds("n1"), NullLoggerFactory.Instance, channelLifetimeSeconds: 600,
            // rebind-delay → (fail) retry-backoff → rebind-delay → second attempt.
            delay: ImmediateThenBlock(immediate: 3, new List<TimeSpan>()));
        loop.Start();

        await retried.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(Volatile.Read(ref calls) >= 2);
    }

    [Fact]
    public async Task Dispose_stops_the_loop_and_issues_no_teardown()
    {
        var calls = 0;
        var firstRebind = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<StunCredentials?> Rebind(StunCredentials? creds, CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            firstRebind.TrySetResult();
            return Task.FromResult<StunCredentials?>(creds);
        }

        var loop = new TurnChannelRebindLoop(
            Rebind, Creds("n1"), NullLoggerFactory.Instance, channelLifetimeSeconds: 600,
            delay: ImmediateThenBlock(immediate: 1, new List<TimeSpan>()));
        loop.Start();

        await firstRebind.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var beforeDispose = Volatile.Read(ref calls);
        await loop.DisposeAsync();

        // A channel binding has no teardown; disposal issues no extra re-bind — the call count must not grow.
        await Task.Delay(50);
        Assert.Equal(beforeDispose, Volatile.Read(ref calls));
    }
}
